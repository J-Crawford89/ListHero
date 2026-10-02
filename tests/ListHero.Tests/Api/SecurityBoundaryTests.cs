using System.Net;
using System.Net.Http.Json;
using ListHero.Contracts.Lists;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [Theory]
    [InlineData("not-base64", 400)] [InlineData("AA==", 400)] [InlineData("AAAAAAAAAAA=", 409)]
    public async Task Invalid_or_stale_row_versions_are_rejected_without_modifying_the_record(string version, int status)
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        var list = await CreateListAsync(owner);
        var path = $"/api/lists/{list.Id}";
        var response = await owner.PutAsJsonAsync(path, new UpdateWishListRequest { Name = "Rejected change", RowVersion = version });
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(list.Name, (await owner.GetFromJsonAsync<OwnerWishListResponse>(path + "/owner"))!.Name);
    }
    [Theory]
    [InlineData("anonymous")]
    [InlineData("user")]
    [InlineData("admin")]
    [InlineData("missing-scope")]
    public async Task Every_owner_operation_rejects_nonowners_even_with_a_share_link(string caller)
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        using var outsider = caller == "anonymous" ? factory.CreateClient(new() { BaseAddress = new("https://localhost") }) : SignedInClient(factory);
        if (caller == "admin") outsider.DefaultRequestHeaders.Add("X-Test-Role", "Admin");
        if (caller == "missing-scope") outsider.DefaultRequestHeaders.Add("X-Test-Scope", "unrelated_scope");
        var list = await CreateListAsync(owner);
        var item = list.Items[0];
        var path = $"/api/lists/{list.Id}";
        var link = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync(path + "/share-links/", new CreateShareLinkRequest()));
        outsider.DefaultRequestHeaders.Add(CapabilityHeaders.Share, link.Token);
        var requests = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, path + "/owner", null),
            (HttpMethod.Post, path + "/items", new CreateWishListItemRequest { Name = "Intrusion" }),
            (HttpMethod.Put, path, new UpdateWishListRequest { Name = "Intrusion", RowVersion = list.RowVersion }),
            (HttpMethod.Post, path + "/archive", new ArchiveRequest { RowVersion = list.RowVersion }),
            (HttpMethod.Put, path + $"/items/{item.Id}", new UpdateWishListItemRequest { Name = "Intrusion", RowVersion = item.RowVersion }),
            (HttpMethod.Post, path + $"/items/{item.Id}/archive", new ArchiveRequest { RowVersion = item.RowVersion }),
            (HttpMethod.Get, path + $"/items/{item.Id}/edit-warning", null),
            (HttpMethod.Get, path + "/share-links/", null),
            (HttpMethod.Post, path + "/share-links/", new CreateShareLinkRequest()),
            (HttpMethod.Post, path + $"/share-links/{link.Id}/revoke", null)
        };
        foreach (var operation in requests)
        {
            using var request = new HttpRequestMessage(operation.Method, operation.Path);
            if (operation.Body is not null) request.Content = JsonContent.Create(operation.Body, operation.Body.GetType());
            using var response = await outsider.SendAsync(request);
            var expected = caller == "anonymous" ? HttpStatusCode.Unauthorized
                : caller == "missing-scope" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound;
            Assert.True(response.StatusCode == expected, $"{caller}: {operation.Method} {operation.Path} returned {response.StatusCode}, expected {expected}.");
        }
        var saved = await owner.GetFromJsonAsync<OwnerWishListResponse>(path + "/owner");
        Assert.Equal(list, saved! with { Items = list.Items });
        Assert.Single(saved!.Items);
        Assert.Equal(item, saved.Items[0]);
        Assert.True(Assert.Single((await owner.GetFromJsonAsync<ShareLinkResponse[]>(path + "/share-links/"))!).IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forged_or_archived_guest_credentials_never_establish_mark_ownership(bool archived)
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        using var guest = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var list = await CreateListAsync(owner);
        var path = $"/api/lists/{list.Id}";
        var link = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync(path + "/share-links/", new CreateShareLinkRequest()));
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Share, link.Token);
        var credential = await JsonAsync<GuestCredentialResponse>(await guest.PostAsync(path + "/guest-credential", null));
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Guest, credential.Credential);
        var mark = await JsonAsync<OwnPurchaseMarkResponse>(await guest.PostAsJsonAsync(path + $"/items/{list.Items[0].Id}/marks", new CreatePurchaseMarkRequest { IdempotencyKey = Guid.NewGuid() }));
        if (archived) factory.Services.GetRequiredService<MemoryCollaborationStore>().Guests.Single().Value.Archive(DateTimeOffset.UtcNow);
        else { guest.DefaultRequestHeaders.Remove(CapabilityHeaders.Guest); guest.DefaultRequestHeaders.Add(CapabilityHeaders.Guest, "forged-guest-key"); }
        var view = (await guest.GetFromJsonAsync<ListViewResponse>(path + "/view"))!.Viewer!;
        Assert.Empty(view.MyMarks!);
        Assert.Equal(1, view.Items[0].PurchasedQuantity);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync(path + $"/items/{mark.ItemId}/marks", new CreatePurchaseMarkRequest { IdempotencyKey = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync(path + $"/items/{mark.ItemId}/marks/{mark.Id}/undo", new ArchiveRequest { RowVersion = mark.RowVersion })).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_valid_actor_cannot_reuse_a_mark_key_to_claim_or_discover_it(bool signedIn)
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        using var first = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var second = signedIn ? SignedInClient(factory) : factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var list = await CreateListAsync(owner);
        var path = $"/api/lists/{list.Id}";
        var link = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync(path + "/share-links/", new CreateShareLinkRequest()));
        foreach (var buyer in new[] { first, second }) buyer.DefaultRequestHeaders.Add(CapabilityHeaders.Share, link.Token);
        var firstCredential = await JsonAsync<GuestCredentialResponse>(await first.PostAsync(path + "/guest-credential", null));
        first.DefaultRequestHeaders.Add(CapabilityHeaders.Guest, firstCredential.Credential);
        if (!signedIn)
        {
            var secondCredential = await JsonAsync<GuestCredentialResponse>(await second.PostAsync(path + "/guest-credential", null));
            second.DefaultRequestHeaders.Add(CapabilityHeaders.Guest, secondCredential.Credential);
        }
        var request = new CreatePurchaseMarkRequest { Quantity = 2, IdempotencyKey = Guid.NewGuid() };
        var mark = await JsonAsync<OwnPurchaseMarkResponse>(await first.PostAsJsonAsync(path + $"/items/{list.Items[0].Id}/marks", request));
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync(path + $"/items/{mark.ItemId}/marks", request)).StatusCode);
        Assert.Empty((await second.GetFromJsonAsync<ListViewResponse>(path + "/view"))!.Viewer!.MyMarks!);
        Assert.Single(factory.Services.GetRequiredService<MemoryCollaborationStore>().Marks);
    }

    [Fact]
    public async Task Resource_identifiers_cannot_be_mixed_between_lists_or_items()
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        using var buyer = SignedInClient(factory);
        var first = await CreateListAsync(owner);
        var second = await CreateListAsync(owner);
        foreach (var list in new[] { first, second })
            await owner.PutAsJsonAsync($"/api/lists/{list.Id}", new UpdateWishListRequest { Name = list.Name, IsPublic = true, RowVersion = list.RowVersion });
        var foreign = second.Items[0];
        var path = $"/api/lists/{first.Id}/items/{foreign.Id}";
        var mark = await JsonAsync<OwnPurchaseMarkResponse>(await buyer.PostAsJsonAsync($"/api/lists/{second.Id}/items/{foreign.Id}/marks", new CreatePurchaseMarkRequest { IdempotencyKey = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.PostAsJsonAsync(path + "/marks", new CreatePurchaseMarkRequest { IdempotencyKey = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.PostAsJsonAsync(path + $"/marks/{mark.Id}/undo", new ArchiveRequest { RowVersion = mark.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.PostAsJsonAsync($"/api/lists/{first.Id}/items/{first.Items[0].Id}/marks/{mark.Id}/undo", new ArchiveRequest { RowVersion = mark.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync(path, new UpdateWishListItemRequest { Name = "Intrusion", RowVersion = foreign.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync(path + "/archive", new ArchiveRequest { RowVersion = foreign.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(path + "/edit-warning")).StatusCode);
        var link = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync($"/api/lists/{second.Id}/share-links/", new CreateShareLinkRequest()));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/lists/{first.Id}/share-links/{link.Id}/revoke", null)).StatusCode);
        Assert.True(Assert.Single((await owner.GetFromJsonAsync<ShareLinkResponse[]>($"/api/lists/{second.Id}/share-links/"))!).IsActive);
        Assert.Single((await buyer.GetFromJsonAsync<ListViewResponse>($"/api/lists/{second.Id}/view"))!.Viewer!.MyMarks!);
    }
}

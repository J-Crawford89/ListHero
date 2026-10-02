using Bunit;
using ListHero.Client.Abstractions;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Lists;
using ListHero.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.UI;

public sealed partial class OwnerPageTests
{
    [Fact]
    public void Create_form_validates_then_navigates_to_the_persisted_list()
    {
        using var context = CreateContext(out var api);
        var page = context.Render<MyLists>();
        page.WaitForAssertion(() => Assert.Contains("Your first wish", page.Markup));
        page.Find("form").Submit();
        Assert.Null(api.Created);
        page.Find("#list-name").Change("Birthday");
        page.Find("#list-description").Change("A few wishes");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.EndsWith($"/lists/{api.ListId}", context.Services.GetRequiredService<NavigationManager>().Uri));
        Assert.Equal("Birthday", api.Created!.Name);
        Assert.Equal("A few wishes", api.Created.Description);
    }

    [Fact]
    public void Add_form_rejects_invalid_quantity_then_displays_the_saved_item_and_resets()
    {
        using var context = CreateContext(out var api);
        var page = context.Render<OwnerList>(parameters => parameters.Add(component => component.ListId, api.ListId));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#add-item-form")));
        page.Find("#item-name").Change("Mug");
        page.Find("#item-quantity").Change("0");
        page.Find("#add-item-form").Submit();
        Assert.Null(api.Added);
        page.Find("#item-quantity").Change("2");
        page.Find("#item-price").Change("24.50");
        page.Find("#item-priority").Change("3");
        page.Find("#item-order").Change("10");
        page.Find("#item-link").Change("https://example.com/mug");
        page.Find("#add-item-form").Submit();
        page.WaitForAssertion(() => Assert.Equal("Mug", page.Find(".wish-card h2").TextContent));
        Assert.Equal(2, api.Added!.DesiredQuantity);
        Assert.Equal(24.50, api.Added.ApproximateUnitPrice);
        Assert.Equal(3, api.Added.Priority);
        Assert.Equal(10, api.Added.DisplayOrder);
        Assert.Equal("https://example.com/mug", page.Find(".wish-card a").GetAttribute("href"));
        Assert.Equal(string.Empty, page.Find("#item-name").GetAttribute("value") ?? string.Empty);
        Assert.Equal("1", page.Find("#item-quantity").GetAttribute("value"));
        Assert.DoesNotContain("purchased", page.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expired_authentication_offers_sign_in_without_crashing_the_component()
    {
        using var context = CreateContext(out var api);
        api.RequiresSignIn = true;
        var page = context.Render<MyLists>();
        page.WaitForAssertion(() => Assert.Contains("Please sign in again", page.Find("[role=alert]").TextContent));
        Assert.Equal("/account/sign-in", page.Find("[role=alert] a").GetAttribute("href"));
    }

    [Fact]
    public void Changing_routes_while_a_request_is_pending_never_displays_the_previous_list()
    {
        using var context = CreateContext(out var api);
        var oldId = api.ListId;
        var newId = Guid.NewGuid();
        var pending = new TaskCompletionSource<OwnerWishListResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Read = id => id == oldId ? pending.Task : Task.FromResult(new OwnerWishListResponse(newId, "New list", "", false, []));
        var page = context.Render<OwnerList>(parameters => parameters.Add(component => component.ListId, oldId));
        page.WaitForAssertion(() => Assert.Contains("Loading your list", page.Markup));
        page.Render(parameters => parameters.Add(component => component.ListId, newId));
        pending.SetResult(new(oldId, "Old list", "", false, []));
        page.WaitForAssertion(() => Assert.Equal("New list", page.Find("h1").TextContent));
        Assert.DoesNotContain("Old list", page.Markup);
    }

    [Fact]
    public void Editing_warns_about_existing_marks_and_keeps_the_draft_on_conflict()
    {
        using var context = CreateContext(out var api);
        var itemId = Guid.NewGuid();
        api.Current = new(api.ListId, "Birthday", "", false, [new(itemId, "Mug", "", 20, 1, 0, 0, null, null, "AQAAAAAAAAA=")], "AQAAAAAAAAA=");
        api.HasMarks = true;
        var page = context.Render<OwnerList>(parameters => parameters.Add(component => component.ListId, api.ListId));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".edit-item")));
        page.Find(".edit-item").Click();
        page.WaitForAssertion(() => Assert.Contains("Someone may already", page.Markup));
        page.Find("#edit-item-name").Change("New mug");
        api.Conflict = true;
        page.Find("#edit-item-form").Submit();
        page.WaitForAssertion(() => Assert.Contains("This record changed", page.Find("[role=alert]").TextContent));
        Assert.Equal("New mug", page.Find("#edit-item-name").GetAttribute("value"));
        api.Conflict = false;
        page.Find("#edit-item-form").Submit();
        page.WaitForAssertion(() => Assert.Equal("New mug", page.Find(".wish-card h2").TextContent));
        Assert.Empty(page.FindAll("#edit-item-form"));
    }

    [Fact]
    public void Share_addresses_use_fragments_and_revoked_links_stop_displaying_the_token()
    {
        using var context = CreateContext(out var api);
        var page = context.Render<ListHero.UI.Components.ShareLinkManager>(parameters => parameters.Add(component => component.ListId, api.ListId));
        page.WaitForAssertion(() => Assert.Contains("No share links yet", page.Markup));
        page.Find("#create-share-link").Submit();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".share-entry input")));
        var url = new Uri(page.Find(".share-entry input").GetAttribute("value")!);
        Assert.Empty(url.Query);
        Assert.Equal("#test-share-token", url.Fragment);
        page.Find(".share-entry button").Click();
        page.WaitForAssertion(() => Assert.Contains("Revoked", page.Markup));
        Assert.DoesNotContain("test-share-token", page.Markup);
    }

    private static BunitContext CreateContext(out TestWishListApi api)
    {
        var context = new BunitContext();
        api = new();
        context.Services.AddSingleton<IWishListApi>(api);
        context.Services.AddSingleton<IHostPresentation>(new TestHost());
        context.Services.AddSingleton<IListCollaborationApi>(new TestCollaborationApi());
        context.Services.AddSingleton<IListShareUrlBuilder>(new ListHero.Client.Services.ListShareUrlBuilder(new Uri("https://localhost:7016/")));
        return context;
    }

    private sealed class TestHost : IHostPresentation
    {
        public bool CanSignIn => true;
        public bool IsDevelopment => true;
        public string SignInUrl => "/account/sign-in";
        public string SignOutUrl => "/account/sign-out";
    }

    private sealed class TestWishListApi : IWishListApi
    {
        public Guid ListId { get; } = Guid.NewGuid();
        public CreateWishListRequest? Created { get; private set; }
        public CreateWishListItemRequest? Added { get; private set; }
        public bool RequiresSignIn { get; set; }
        public Func<Guid, Task<OwnerWishListResponse>>? Read { get; set; }
        public OwnerWishListResponse? Current { get; set; }
        public bool HasMarks { get; set; }
        public bool Conflict { get; set; }
        public UpdateWishListRequest? UpdatedList { get; private set; }
        public UpdateWishListItemRequest? UpdatedItem { get; private set; }
        public ArchiveRequest? ArchivedList { get; private set; }
        public ArchiveRequest? ArchivedItem { get; private set; }
        public Task<IReadOnlyList<WishListSummaryResponse>> GetMineAsync(CancellationToken cancellationToken = default)
            => RequiresSignIn ? throw new SignInRequiredException() : Task.FromResult<IReadOnlyList<WishListSummaryResponse>>([]);
        public Task<OwnerWishListResponse> CreateAsync(CreateWishListRequest request, CancellationToken cancellationToken = default)
        {
            Created = request;
            return Task.FromResult(new OwnerWishListResponse(ListId, request.Name, request.Description, false, []));
        }
        public Task<OwnerWishListResponse> GetOwnerViewAsync(Guid listId, CancellationToken cancellationToken = default)
            => Read?.Invoke(listId) ?? Task.FromResult(Current ?? new OwnerWishListResponse(listId, "Birthday", "", false, []));
        public Task<OwnerWishListResponse> AddItemAsync(Guid listId, CreateWishListItemRequest request, CancellationToken cancellationToken = default)
        {
            Added = request;
            WishListItemResponse item = new(Guid.NewGuid(), request.Name, request.Description, request.ApproximateUnitPrice,
                request.DesiredQuantity, request.Priority, request.DisplayOrder, request.Url, request.ImageUrl);
            return Task.FromResult(new OwnerWishListResponse(listId, "Birthday", "", false, [item]));
        }
        public Task<OwnerWishListResponse> UpdateAsync(Guid listId, UpdateWishListRequest request, CancellationToken cancellationToken = default)
        {
            UpdatedList = request;
            if (Conflict || request.RowVersion != Current!.RowVersion) throw new ApiConflictException();
            return Task.FromResult(Current = Current with { Name = request.Name, Description = request.Description, IsPublic = request.IsPublic });
        }
        public Task ArchiveAsync(Guid listId, ArchiveRequest request, CancellationToken cancellationToken = default)
        { ArchivedList = request; return Task.CompletedTask; }
        public Task<OwnerWishListResponse> UpdateItemAsync(Guid listId, Guid itemId, UpdateWishListItemRequest request, CancellationToken cancellationToken = default)
        {
            UpdatedItem = request;
            if (Conflict || request.RowVersion != Current!.Items.Single(item => item.Id == itemId).RowVersion) throw new ApiConflictException();
            return Task.FromResult(Current = Current with { Items = [Current.Items.Single() with { Name = request.Name,
                Description = request.Description, DesiredQuantity = request.DesiredQuantity, ApproximateUnitPrice = request.ApproximateUnitPrice,
                Priority = request.Priority, DisplayOrder = request.DisplayOrder, Url = request.Url, ImageUrl = request.ImageUrl }] });
        }
        public Task<OwnerWishListResponse> ArchiveItemAsync(Guid listId, Guid itemId, ArchiveRequest request, CancellationToken cancellationToken = default)
        { ArchivedItem = request; return Task.FromResult(Current = Current! with { Items = Current.Items.Where(item => item.Id != itemId).ToArray() }); }
        public Task<ItemEditWarningResponse> EditWarningAsync(Guid listId, Guid itemId, CancellationToken cancellationToken = default) => Task.FromResult(new ItemEditWarningResponse(HasMarks));
    }
    internal sealed class TestCollaborationApi : IListCollaborationApi
    {
        private readonly List<ShareLinkResponse> links = [];
        public Task<IReadOnlyList<ShareLinkResponse>> GetLinksAsync(Guid listId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ShareLinkResponse>>(links.ToArray());
        public Task<ShareLinkResponse> CreateLinkAsync(Guid listId, CreateShareLinkRequest request, CancellationToken ct = default)
        {
            var link = new ShareLinkResponse(Guid.NewGuid(), "test-share-token", DateTimeOffset.UtcNow, request.ExpiresAt, null, true);
            links.Add(link); return Task.FromResult(link);
        }
        public Task RevokeLinkAsync(Guid listId, Guid linkId, CancellationToken ct = default)
        {
            var index = links.FindIndex(link => link.Id == linkId);
            links[index] = links[index] with { Token = null, RevokedAt = DateTimeOffset.UtcNow, IsActive = false };
            return Task.CompletedTask;
        }
        public Task<ListViewResponse> ViewAsync(Guid listId, ViewerCredentials credentials, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GuestCredentialResponse> IssueGuestAsync(Guid listId, ViewerCredentials credentials, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OwnPurchaseMarkResponse> MarkAsync(Guid listId, Guid itemId, CreatePurchaseMarkRequest request, ViewerCredentials credentials, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UndoAsync(Guid listId, Guid itemId, Guid markId, ArchiveRequest request, ViewerCredentials credentials, CancellationToken ct = default) => throw new NotImplementedException();
    }
}

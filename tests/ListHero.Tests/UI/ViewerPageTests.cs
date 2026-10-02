using Bunit;
using ListHero.Client.Abstractions;
using ListHero.Client.Services;
using ListHero.Contracts.Lists;
using ListHero.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.UI;

public sealed partial class ViewerPageTests
{
    [Fact]
    public void Anonymous_mark_retry_keeps_the_same_key_and_quantity_then_can_be_undone()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton(TimeProvider.System);
        context.AddAuthorization().SetNotAuthorized();
        var fake = new FakeViewer { FailOnce = true };
        context.Services.AddSingleton<IListViewerClient>(fake);
        context.Services.AddSingleton<IHostPresentation>(new FakeHost());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/view/{fake.ListId}#test-share-token");
        var page = context.Render<ViewerList>(parameters => parameters.Add(component => component.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".mark-form")));
        page.Find("input[type=number]").Change("6");
        page.Find(".mark-form").Submit();
        page.WaitForAssertion(() => Assert.Contains("couldn't reach", page.Find("[role=alert]").TextContent));
        Assert.True(page.Find("input[type=number]").HasAttribute("disabled"));
        page.Find(".mark-form").Submit();
        page.WaitForAssertion(() => Assert.Contains("Fulfilled", page.Markup));
        Assert.Equal(2, fake.Attempts.Count);
        Assert.Equal(fake.Attempts[0], fake.Attempts[1]);
        Assert.All(fake.Attempts, attempt => Assert.Equal(6, attempt.Quantity));
        Assert.Equal("test-share-token", fake.ShareToken);
        Assert.False(fake.SignedIn);
        Assert.Contains("2 over", page.Markup);
        page.Find(".own-mark button").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".own-mark")));
        Assert.DoesNotContain("Fulfilled", page.Markup);
    }

    [Fact]
    public void Public_anonymous_view_does_not_render_purchase_actions()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton(TimeProvider.System);
        context.AddAuthorization().SetNotAuthorized();
        var fake = new FakeViewer { CanMark = false };
        context.Services.AddSingleton<IListViewerClient>(fake);
        context.Services.AddSingleton<IHostPresentation>(new FakeHost());
        var page = context.Render<ViewerList>(parameters => parameters.Add(component => component.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.Contains("Sign in or use an active share link", page.Markup));
        Assert.Empty(page.FindAll(".mark-form"));
        Assert.Equal($"/account/sign-in?listId={fake.ListId}", page.Find("a.button-secondary").GetAttribute("href"));
    }
    private sealed class FakeHost : IHostPresentation
    {
        public bool CanSignIn => true;
        public bool IsDevelopment => true;
        public string SignInUrl => "/account/sign-in";
        public string SignOutUrl => "/account/sign-out";
    }
    private sealed class FakeViewer : IListViewerClient
    {
        public Guid ListId { get; } = Guid.NewGuid();
        public Guid ItemId { get; } = Guid.NewGuid();
        public bool CanMark { get; set; } = true;
        public bool FailOnce { get; set; }
        public bool SignedIn { get; private set; }
        public string? ShareToken { get; private set; }
        public List<(Guid Key, int Quantity)> Attempts { get; } = [];
        public OwnPurchaseMarkResponse? mark;
        public Func<Guid, CancellationToken, Task<ListViewResponse>>? Read { get; set; }
        public bool ReturnOwner { get; set; }
        public int Loads { get; private set; }
        public Exception? ReadError { get; set; }
        public bool FailRefreshAfterMark { get; set; }
        public Task<ListViewResponse> LoadAsync(Guid listId, bool signedIn, string? shareToken, CancellationToken ct = default)
        {
            Loads++;
            SignedIn = signedIn; ShareToken = shareToken;
            if (Read is not null) return Read(listId, ct);
            if (ReadError is not null) throw ReadError;
            if (ReturnOwner) return Task.FromResult(new ListViewResponse(new(ListId, "Owner", "", false, []), null));
            WishListItemResponse item = new(ItemId, "Mug", "", null, 4, 0, 0, null, null);
            return Task.FromResult(new ListViewResponse(null, new(ListId, "Birthday", "", true, CanMark,
                [new(item, mark?.Quantity ?? 0, mark?.Quantity >= 4, Math.Max(0, (mark?.Quantity ?? 0) - 4))], mark is null ? [] : [mark])));
        }
        public Task<OwnPurchaseMarkResponse> MarkAsync(Guid listId, Guid itemId, bool signedIn, string? shareToken, CreatePurchaseMarkRequest request, CancellationToken ct = default)
        {
            Attempts.Add((request.IdempotencyKey, request.Quantity));
            if (FailOnce) { FailOnce = false; throw new HttpRequestException(); }
            mark = new(Guid.NewGuid(), ItemId, request.Quantity, DateTimeOffset.UtcNow, null, "AQAAAAAAAAA=");
            if (FailRefreshAfterMark) ReadError = new HttpRequestException();
            return Task.FromResult(mark);
        }
        public Task UndoAsync(Guid listId, OwnPurchaseMarkResponse purchase, bool signedIn, string? shareToken, CancellationToken ct = default)
        { mark = null; return Task.CompletedTask; }
    }
}

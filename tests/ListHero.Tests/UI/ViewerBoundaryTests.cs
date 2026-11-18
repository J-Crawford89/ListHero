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
    public async Task A_temporary_server_failure_keeps_loaded_content_for_retry()
    {
        using var context = ViewerContext(out var fake, out var polling);
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".mark-form")));
        fake.ReadError = new ListHero.Client.Abstractions.Api.ApiRequestException("Please try again.", System.Net.HttpStatusCode.ServiceUnavailable);
        await polling.RefreshAsync();
        page.WaitForAssertion(() =>
        {
            Assert.Single(page.FindAll(".mark-form"));
            Assert.Equal("Birthday", page.Find("h1").TextContent);
            Assert.Contains("try again", page.Find("[role=alert]").TextContent);
        });
    }

    [Fact]
    public void A_late_denial_from_a_previous_route_cannot_clear_the_new_list()
    {
        using var context = ViewerContext(out var fake, out _);
        var old = fake.ListId; var next = Guid.NewGuid();
        var pending = new TaskCompletionSource<ListViewResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Read = (id, _) => id == old ? pending.Task : Task.FromResult(new ListViewResponse(null, new(next, "New list", "", true, false, [], [])));
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/view/{old}");
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, old));
        page.WaitForAssertion(() => Assert.Equal(1, fake.Loads));
        nav.NavigateTo($"/view/{next}");
        page.Render(p => p.Add(c => c.ListId, next));
        pending.SetException(new ListHero.Client.Abstractions.Api.ApiRequestException("Unavailable.", System.Net.HttpStatusCode.NotFound));
        page.WaitForAssertion(() => Assert.Equal("New list", page.Find("h1").TextContent));
    }

    [Theory]
    [InlineData(403, "refresh")] [InlineData(404, "refresh")]
    [InlineData(403, "mark")] [InlineData(404, "mark")]
    [InlineData(403, "undo")] [InlineData(404, "undo")]
    public async Task Definitive_access_denial_clears_previously_loaded_content_and_purchase_actions(int status, string operation)
    {
        using var context = ViewerContext(out var fake, out var polling);
        if (operation == "undo") fake.mark = new(Guid.NewGuid(), fake.ItemId, 1, DateTimeOffset.UtcNow, null, "AQAAAAAAAAA=");
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".mark-form")));
        var error = new ListHero.Client.Abstractions.Api.ApiRequestException("This list is unavailable.", (System.Net.HttpStatusCode)status);
        if (operation == "refresh") { fake.ReadError = error; await polling.RefreshAsync(); }
        else
        {
            fake.WriteError = error;
            if (operation == "mark") page.Find(".mark-form").Submit();
            else page.Find(".own-mark button").Click();
        }
        page.WaitForAssertion(() =>
        {
            Assert.Contains("unavailable", page.Find("[role=alert]").TextContent);
            Assert.Equal("Wish list", page.Find("h1").TextContent);
            Assert.Empty(page.FindAll(".wish-card, .mark-form, .own-mark"));
            Assert.DoesNotContain("Birthday", page.Markup);
        });
        fake.ReadError = null; fake.WriteError = null;
        page.FindAll("button").Single(button => button.TextContent == "Try again").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".mark-form")));
    }

    [Theory]
    [InlineData("%0D%0Ainjected")] [InlineData("bad%20token")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Invalid_fragments_are_rejected_before_any_API_request(string fragment)
    {
        using var context = ViewerContext(out var fake, out _);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/view/{fake.ListId}#{fragment}");
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.Contains("share link is invalid", page.Find("[role=alert]").TextContent));
        Assert.Equal(0, fake.Loads);
        Assert.Empty(page.FindAll(".mark-form"));
    }

    [Fact]
    public void Signed_in_owner_is_redirected_without_rendering_viewer_purchase_data()
    {
        using var context = ViewerContext(out var fake, out _);
        context.AddAuthorization().SetAuthorized("Owner");
        fake.ReturnOwner = true;
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.EndsWith($"/lists/{fake.ListId}", context.Services.GetRequiredService<NavigationManager>().Uri));
        Assert.True(fake.SignedIn);
        Assert.Empty(page.FindAll(".fulfillment"));
    }

    [Fact]
    public void Navigation_discards_a_pending_response_from_the_previous_list()
    {
        using var context = ViewerContext(out var fake, out _);
        var old = fake.ListId;
        var next = Guid.NewGuid();
        var pending = new TaskCompletionSource<ListViewResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Read = (id, _) => id == old ? pending.Task : Task.FromResult(new ListViewResponse(null, new(next, "New list", "", true, false, [], [])));
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/view/{old}#old-token");
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, old));
        page.WaitForAssertion(() => Assert.Equal(1, fake.Loads));
        nav.NavigateTo($"/view/{next}#new-token");
        page.Render(p => p.Add(c => c.ListId, next));
        pending.SetResult(new(null, new(old, "Old list", "", false, true, [], [])));
        page.WaitForAssertion(() => Assert.Equal("New list", page.Find("h1").TextContent));
        Assert.DoesNotContain("Old list", page.Markup);
        Assert.Equal("new-token", fake.ShareToken);
    }

    [Fact]
    public async Task Polling_refreshes_other_buyers_marks_removes_actions_after_revocation_and_stops_on_disposal()
    {
        using var context = ViewerContext(out var fake, out var polling);
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".mark-form")));
        fake.mark = new(Guid.NewGuid(), fake.ItemId, 4, DateTimeOffset.UtcNow, null, "AQAAAAAAAAA=");
        await polling.RefreshAsync();
        page.WaitForAssertion(() => Assert.Contains("Fulfilled", page.Markup));
        Assert.Single(page.FindAll(".own-mark button"));
        fake.CanMark = false;
        await polling.RefreshAsync();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".mark-form")));
        Assert.Single(page.FindAll(".own-mark"));
        Assert.Empty(page.FindAll(".own-mark button"));
        await page.Instance.DisposeAsync();
        Assert.True(polling.Stopped);
        var calls = fake.Loads;
        await polling.RefreshAsync();
        Assert.Equal(calls, fake.Loads);
    }

    [Fact]
    public void Refresh_failure_after_a_saved_mark_does_not_resubmit_the_mark()
    {
        using var context = ViewerContext(out var fake, out _);
        fake.FailRefreshAfterMark = true;
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".mark-form")));
        page.Find(".mark-form").Submit();
        page.WaitForAssertion(() => Assert.Contains("couldn't reach", page.Find("[role=alert]").TextContent));
        Assert.Contains("You marked 1", page.Markup);
        Assert.Single(fake.Attempts);
        fake.ReadError = null;
        page.FindAll("button").Single(b => b.TextContent == "Refresh list").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".own-mark")));
        Assert.Single(fake.Attempts);
    }

    [Fact]
    public async Task Request_timeout_offers_retry_and_disposing_cancels_a_pending_load()
    {
        using var context = ViewerContext(out var fake, out _);
        fake.ReadError = new OperationCanceledException();
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.Contains("took too long", page.Find("[role=alert]").TextContent));
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ReadError = null;
        fake.Read = async (_, ct) => { started.SetResult(ct); await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); };
        page.FindAll("button").Single(b => b.TextContent == "Try again").Click();
        page.WaitForAssertion(() => Assert.True(started.Task.IsCompleted));
        page.Instance.Dispose();
        Assert.True((await started.Task).IsCancellationRequested);
    }

    private static BunitContext ViewerContext(out FakeViewer fake, out TestPolling polling)
    {
        var context = new BunitContext();
        context.AddAuthorization().SetNotAuthorized();
        fake = new(); polling = new();
        context.Services.AddSingleton<IListViewerClient>(fake);
        context.Services.AddSingleton<IHostPresentation>(new FakeHost());
        context.Services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(polling);
        return context;
    }
}

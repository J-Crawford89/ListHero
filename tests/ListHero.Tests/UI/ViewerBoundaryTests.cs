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
    public void Polling_refreshes_other_buyers_marks_removes_actions_after_revocation_and_stops_on_disposal()
    {
        using var context = ViewerContext(out var fake, out var clock);
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".mark-form")));
        fake.mark = new(Guid.NewGuid(), fake.ItemId, 4, DateTimeOffset.UtcNow, null, "AQAAAAAAAAA=");
        clock.Tick();
        page.WaitForAssertion(() => Assert.Contains("Fulfilled", page.Markup));
        Assert.Single(page.FindAll(".own-mark button"));
        fake.CanMark = false;
        clock.Tick();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".mark-form")));
        Assert.Single(page.FindAll(".own-mark"));
        Assert.Empty(page.FindAll(".own-mark button"));
        page.Instance.Dispose();
        page.WaitForAssertion(() => Assert.True(clock.TimerDisposed));
        var calls = fake.Loads;
        clock.Tick();
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

    private static BunitContext ViewerContext(out FakeViewer fake, out ManualClock clock)
    {
        var context = new BunitContext();
        context.AddAuthorization().SetNotAuthorized();
        fake = new(); clock = new();
        context.Services.AddSingleton<IListViewerClient>(fake);
        context.Services.AddSingleton<IHostPresentation>(new FakeHost());
        context.Services.AddSingleton<TimeProvider>(clock);
        return context;
    }
    private sealed class ManualClock : TimeProvider
    {
        private ManualTimer? timer;
        public bool TimerDisposed => timer?.Disposed == true;
        public void Tick() => timer?.Tick();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => timer = new(callback, state);
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Tick() { if (!Disposed) callback(state); }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

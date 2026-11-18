using Bunit;
using ListHero.UI.Pages;
using Microsoft.JSInterop;

namespace ListHero.Tests.UI;

public sealed partial class ViewerPageTests
{
    [Fact]
    public async Task Polling_does_not_overlap_a_request_or_automatically_retry_a_failed_request()
    {
        using var context = ViewerContext(out var fake, out var polling);
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.True(polling.Started));
        var pending = new TaskCompletionSource<ListHero.Contracts.Lists.ListViewResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Read = (_, _) => pending.Task;
        var refresh = polling.RefreshAsync();
        page.WaitForAssertion(() => Assert.Equal(2, fake.Loads));
        await polling.RefreshAsync();
        Assert.Equal(2, fake.Loads);
        pending.SetException(new HttpRequestException());
        await refresh;
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[role=alert]")));
        await polling.RefreshAsync();
        Assert.Equal(2, fake.Loads);
        fake.Read = null;
        page.FindAll("button").Single(b => b.TextContent == "Refresh list").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[role=alert]")));
        await polling.RefreshAsync();
        Assert.Equal(4, fake.Loads);
    }

    [Fact]
    public void A_polling_module_failure_keeps_manual_refresh_available()
    {
        using var context = ViewerContext(out var fake, out var polling);
        polling.FailImport = true;
        var page = context.Render<ViewerList>(p => p.Add(c => c.ListId, fake.ListId));
        page.WaitForAssertion(() => Assert.Contains("Automatic updates are unavailable", page.Markup));
        page.FindAll("button").Single(b => b.TextContent == "Refresh list").Click();
        page.WaitForAssertion(() => Assert.Equal(2, fake.Loads));
    }

    private sealed class TestPolling : IJSRuntime, IJSObjectReference
    {
        private ViewerList? viewer;
        public bool Started => viewer is not null;
        public bool Stopped { get; private set; }
        public bool FailImport { get; set; }
        public Task RefreshAsync() => Stopped || viewer is null ? Task.CompletedTask : viewer.RefreshViewer();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import" && FailImport) throw new JSException("Module could not load");
            if (identifier == "watch") viewer = ((DotNetObjectReference<ViewerList>)args![0]!).Value;
            if (identifier == "dispose") { Stopped = true; return ValueTask.FromResult(default(TValue)!); }
            Assert.Contains(identifier, new[] { "import", "watch" });
            return ValueTask.FromResult((TValue)(object)this);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

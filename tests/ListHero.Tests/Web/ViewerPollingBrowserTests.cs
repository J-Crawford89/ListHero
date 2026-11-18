using Microsoft.Playwright;

namespace ListHero.Tests.Web;

public sealed partial class WebHostTests
{
    private static async Task VerifyViewerPollingAsync(IBrowser browser, string origin)
    {
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        // A static same-origin document isolates the module clock from Blazor/SignalR timers.
        await page.GotoAsync(origin + "/_content/ListHero.UI/images/list-hero-emblem.svg");
        var clockStart = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        await page.Clock.InstallAsync(new() { Time = clockStart.ToString("O") });
        await page.Clock.PauseAtAsync(clockStart.AddSeconds(1));
        await page.EvaluateAsync("""
            async () => {
                window.testHidden = false;
                window.testOnline = true;
                Object.defineProperty(document, 'hidden', { get: () => window.testHidden });
                Object.defineProperty(navigator, 'onLine', { get: () => window.testOnline });
                window.pollCalls = 0;
                const module = await import('/_content/ListHero.UI/Pages/ViewerList.razor.js');
                window.polling = module.watch({
                    invokeMethodAsync: async method => {
                        if (method !== 'RefreshViewer') throw new Error('Unexpected polling callback');
                        window.pollCalls++;
                        if (window.failPolling) throw new Error('Disconnected circuit');
                        if (window.holdPolling) await new Promise(resolve => { window.releasePolling = resolve; });
                    }
                });
            }
            """);
        await page.Clock.RunForAsync(19999);
        Assert.Equal(0, await page.EvaluateAsync<int>("window.pollCalls"));
        await page.Clock.RunForAsync(1);
        Assert.Equal(1, await page.EvaluateAsync<int>("window.pollCalls"));
        await page.EvaluateAsync("window.testHidden = true; document.dispatchEvent(new Event('visibilitychange'))");
        await page.Clock.RunForAsync(60000);
        Assert.Equal(1, await page.EvaluateAsync<int>("window.pollCalls"));
        await page.EvaluateAsync("window.testOnline = false; window.dispatchEvent(new Event('offline')); window.testHidden = false; document.dispatchEvent(new Event('visibilitychange'))");
        await page.Clock.RunForAsync(60000);
        Assert.Equal(1, await page.EvaluateAsync<int>("window.pollCalls"));
        await page.EvaluateAsync("window.testOnline = true; window.dispatchEvent(new Event('online'))");
        Assert.Equal(2, await page.EvaluateAsync<int>("window.pollCalls"));
        await page.EvaluateAsync("window.dispatchEvent(new Event('focus'))");
        Assert.Equal(3, await page.EvaluateAsync<int>("window.pollCalls"));
        await page.Clock.RunForAsync(20000);
        Assert.Equal(4, await page.EvaluateAsync<int>("window.pollCalls"));

        await page.EvaluateAsync("window.holdPolling = true; window.dispatchEvent(new Event('focus'))");
        await page.EvaluateAsync("window.dispatchEvent(new Event('focus'))");
        await page.Clock.RunForAsync(40000);
        Assert.Equal(5, await page.EvaluateAsync<int>("window.pollCalls"));
        await page.EvaluateAsync("window.testHidden = true; document.dispatchEvent(new Event('visibilitychange')); window.holdPolling = false; window.releasePolling()");
        await page.Clock.RunForAsync(40000);
        Assert.Equal(5, await page.EvaluateAsync<int>("window.pollCalls"));
        await page.EvaluateAsync("window.testHidden = false; document.dispatchEvent(new Event('visibilitychange'))");
        Assert.Equal(6, await page.EvaluateAsync<int>("window.pollCalls"));

        await page.EvaluateAsync("window.failPolling = true; window.dispatchEvent(new Event('focus'))");
        await page.Clock.RunForAsync(20000);
        Assert.Equal(8, await page.EvaluateAsync<int>("window.pollCalls"));
        await page.EvaluateAsync("window.polling.dispose(); window.dispatchEvent(new Event('focus')); window.dispatchEvent(new Event('online')); document.dispatchEvent(new Event('visibilitychange'))");
        await page.Clock.RunForAsync(60000);
        Assert.Equal(8, await page.EvaluateAsync<int>("window.pollCalls"));
    }
}

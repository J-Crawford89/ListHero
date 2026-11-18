using Bunit;
using ListHero.Contracts.Discovery;
using ListHero.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.UI;

public sealed partial class DiscoveryPageTests
{
    [Theory]
    [InlineData("abc")] [InlineData("0")] [InlineData("-1")] [InlineData("101")]
    [InlineData("999999999999999999999")] [InlineData("")] [InlineData("1&page=2")]
    [InlineData("1.5")] [InlineData("%201%20")]
    public void Invalid_discovery_pages_offer_a_safe_first_page_without_calling_the_API(string value)
    {
        using var context = Context(out var api);
        Navigate(context, "/discover?tab=people&q=Jane%20%26%20family&page=" + value);
        var page = context.Render<Discover>();
        Assert.Contains("Choose a page from 1–100", page.Find("[role=alert]").TextContent);
        Assert.Equal("/discover?tab=people&q=Jane%20%26%20family&page=1", page.Find("[role=alert] a").GetAttribute("href"));
        Assert.Equal(0, api.Reads);
        Assert.Empty(page.FindAll(".person-card"));
        Navigate(context, page.Find("[role=alert] a").GetAttribute("href")!);
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".person-card")));
        Assert.Equal(1, api.Reads);
    }

    [Theory]
    [InlineData("abc")] [InlineData("0")] [InlineData("101")] [InlineData("1&page=2")]
    public void Invalid_public_profile_pages_can_recover_without_losing_the_person(string value)
    {
        using var context = Context(out var api);
        Navigate(context, "/people/jane?page=" + value);
        var page = context.Render<PublicPerson>(p => p.Add(c => c.Handle, "jane"));
        Assert.Equal(0, api.Reads);
        Assert.Equal("/people/jane?page=1", page.Find("[role=alert] a").GetAttribute("href"));
        Navigate(context, "/people/jane?page=100");
        page.WaitForAssertion(() => Assert.Equal("Jane", page.Find("h1").TextContent));
        Assert.Contains("Page 100", page.Markup);
    }

    [Theory]
    [InlineData("lists")] [InlineData("people")]
    public void Submitting_the_same_search_refreshes_the_results_without_changing_the_URL(string tab)
    {
        using var context = Context(out var api);
        Navigate(context, $"/discover?tab={tab}&q=jane&page=1");
        var page = context.Render<Discover>();
        page.WaitForAssertion(() => Assert.Equal(1, api.Reads));
        var address = context.Services.GetRequiredService<NavigationManager>().Uri;
        api.Empty = true;
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Equal(2, api.Reads));
        Assert.Contains(tab == "people" ? "No people found" : "No public lists found", page.Markup);
        Assert.Equal(address, context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task A_repeat_search_cancels_the_previous_read_and_discards_its_late_response()
    {
        using var context = Context(out var api);
        Navigate(context, "/discover?tab=lists&q=jane&page=1");
        var pending = new TaskCompletionSource<DiscoveryPage<PublicListSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Pending = pending.Task;
        var page = context.Render<Discover>();
        page.WaitForAssertion(() => Assert.Equal(1, api.Reads));
        var oldCancellation = api.LastReadCancellation;
        api.Pending = null; api.Empty = true;
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("No public lists found", page.Markup));
        Assert.True(oldCancellation.IsCancellationRequested);
        pending.SetResult(new([new(api.Id, "Old list", "", DateTimeOffset.UtcNow, new("Jane", "jane"))], 1, 20, false));
        await page.InvokeAsync(async () => await Task.Yield());
        Assert.DoesNotContain("Old list", page.Markup);
        Assert.Contains("No public lists found", page.Markup);
    }

    [Fact]
    public async Task Navigating_to_an_invalid_page_cancels_and_invalidates_an_inflight_read()
    {
        using var context = Context(out var api);
        var pending = new TaskCompletionSource<DiscoveryPage<PublicListSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Pending = pending.Task;
        var page = context.Render<Discover>();
        page.WaitForAssertion(() => Assert.Equal(1, api.Reads));
        var oldCancellation = api.LastReadCancellation;
        Navigate(context, "/discover?page=broken");
        Assert.True(oldCancellation.IsCancellationRequested);
        pending.SetResult(new([], 1, 20, false));
        await page.InvokeAsync(async () => await Task.Yield());
        Assert.Contains("Choose a page", page.Find("[role=alert]").TextContent);
        Assert.Empty(page.FindAll(".public-list-card"));
        Assert.DoesNotContain("No public lists found", page.Markup);
    }
}

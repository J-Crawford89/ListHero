using Bunit;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Lists;
using ListHero.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.UI;

public sealed partial class OwnerPageTests
{
    [Fact]
    public void My_lists_defaults_to_fifteen_and_changing_size_resets_the_page_without_losing_the_size_in_links()
    {
        using var context = CreateContext(out var api);
        api.ReadLists = (page, size, _) => Task.FromResult(OwnedPage(page, size));
        var browser = context.Render<OwnedListBrowser>();
        browser.WaitForAssertion(() => Assert.Equal((1, 15), Assert.Single(api.ListRequests)));
        Assert.Equal(new[] { "15", "30", "50", "100" }, browser.FindAll("option").Select(option => option.GetAttribute("value")));
        Assert.Equal("/lists?page=2&pageSize=15", browser.Find("nav a").GetAttribute("href"));
        browser.Render(p => p.Add(c => c.Page, ["3"]).Add(c => c.PageSize, ["30"]));
        browser.WaitForAssertion(() => Assert.Contains("Page 3", browser.Markup));
        Assert.Equal(new[] { "/lists?page=2&pageSize=30", "/lists?page=4&pageSize=30" }, browser.FindAll("nav a").Select(link => link.GetAttribute("href")));
        browser.Find("select").Change("100");
        Assert.EndsWith("/lists?page=1&pageSize=100", context.Services.GetRequiredService<NavigationManager>().Uri);
        browser.Render(p => p.Add(c => c.Page, ["1"]).Add(c => c.PageSize, ["100"]));
        browser.WaitForAssertion(() => Assert.Equal((1, 100), api.ListRequests.Last()));
        Assert.DoesNotContain(browser.FindAll("nav a"), link => link.TextContent.Contains("Previous"));
        browser.Render(p => p.Add(c => c.Page, ["1000"]).Add(c => c.PageSize, ["3"]));
        browser.WaitForAssertion(() => Assert.Equal((1000, 3), api.ListRequests.Last()));
        Assert.Contains(browser.FindAll("option"), option => option.GetAttribute("value") == "3");
        Assert.DoesNotContain(browser.FindAll("nav a"), link => link.TextContent.Contains("Next"));
    }

    [Theory]
    [InlineData("0", "15")] [InlineData("1001", "15")] [InlineData("abc", "15")]
    [InlineData("1", "0")] [InlineData("1", "101")] [InlineData("1", "")]
    [InlineData("1", "15,30")]
    public void Invalid_page_or_size_never_calls_the_API_and_offers_first_page_recovery(string page, string size)
    {
        using var context = CreateContext(out var api);
        var browser = context.Render<OwnedListBrowser>(p => p.Add(c => c.Page, [page]).Add(c => c.PageSize, size.Split(',')));
        Assert.Empty(api.ListRequests);
        Assert.Contains("Choose a page", browser.Find("[role=alert]").TextContent);
        Assert.Equal("/lists?page=1&pageSize=15", browser.Find("a").GetAttribute("href"));
    }

    [Fact]
    public void Duplicate_page_values_are_rejected_without_reading_data()
    {
        using var context = CreateContext(out var api);
        var browser = context.Render<OwnedListBrowser>(p => p.Add(c => c.Page, ["1", "2"]));
        Assert.Empty(api.ListRequests); Assert.Single(browser.FindAll("[role=alert]"));
    }

    [Fact]
    public void Changing_pages_cancels_old_reads_and_late_results_cannot_replace_the_current_page()
    {
        using var context = CreateContext(out var api);
        var pending = new TaskCompletionSource<OwnedListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        api.ReadLists = (page, size, ct) => { if (page == 1) { oldToken = ct; return pending.Task; } return Task.FromResult(OwnedPage(page, size)); };
        var browser = context.Render<OwnedListBrowser>();
        browser.WaitForAssertion(() => Assert.Single(api.ListRequests));
        browser.Render(p => p.Add(c => c.Page, ["2"]));
        browser.WaitForAssertion(() => Assert.Contains("Page 2 wish", browser.Find("h2").TextContent));
        Assert.True(oldToken.IsCancellationRequested);
        pending.SetResult(OwnedPage(1, 15));
        browser.WaitForAssertion(() => Assert.DoesNotContain("Page 1 wish", browser.Markup));
        browser.Render(p => p.Add(c => c.Page, ["2"]));
        Assert.Equal(2, api.ListRequests.Count); // Parent renders must not refetch unchanged paging.
    }

    [Fact]
    public void Empty_later_pages_offer_recovery_instead_of_claiming_the_account_has_no_lists()
    {
        using var context = CreateContext(out _);
        var browser = context.Render<OwnedListBrowser>(p => p.Add(c => c.Page, ["5"]).Add(c => c.PageSize, ["50"]));
        browser.WaitForAssertion(() => Assert.Contains("No lists on this page", browser.Markup));
        Assert.DoesNotContain("Your first wish", browser.Markup);
        Assert.Equal("/lists?page=1&pageSize=50", browser.FindAll("a").Single(link => link.TextContent == "Go to page 1").GetAttribute("href"));
        Assert.DoesNotContain(browser.FindAll("nav a"), link => link.TextContent.Contains("Next"));
    }

    [Fact]
    public void Failed_page_reads_can_be_retried_without_losing_the_selected_page()
    {
        using var context = CreateContext(out var api);
        api.ReadLists = (_, _, _) => throw new HttpRequestException();
        var browser = context.Render<OwnedListBrowser>(p => p.Add(c => c.Page, ["2"]).Add(c => c.PageSize, ["30"]));
        browser.WaitForAssertion(() => Assert.Single(browser.FindAll("[role=alert]")));
        api.ReadLists = (page, size, _) => Task.FromResult(OwnedPage(page, size));
        browser.Find("button").Click();
        browser.WaitForAssertion(() => Assert.Contains("Page 2 wish", browser.Find("h2").TextContent));
        Assert.All(api.ListRequests, request => Assert.Equal((2, 30), request));
    }

    [Fact]
    public void Disposing_the_list_browser_cancels_its_pending_read()
    {
        using var context = CreateContext(out var api);
        var pending = new TaskCompletionSource<OwnedListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        api.ReadLists = (_, _, ct) => { token = ct; return pending.Task; };
        var browser = context.Render<OwnedListBrowser>();
        browser.WaitForAssertion(() => Assert.Single(api.ListRequests));
        browser.Instance.Dispose(); Assert.True(token.IsCancellationRequested);
        pending.SetResult(OwnedPage(1, 15));
    }

    private static OwnedListPage OwnedPage(int page, int size) => new([new(Guid.NewGuid(), $"Page {page} wish", "Description", false, DateTimeOffset.UtcNow)], page, size, true);
}

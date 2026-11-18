using Bunit;
using ListHero.Client.Abstractions;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Lists;
using ListHero.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.UI;

public sealed partial class DashboardPageTests
{
    [Theory]
    [InlineData("abc")] [InlineData("0")] [InlineData("-1")] [InlineData("1001")]
    [InlineData("")] [InlineData("1&page=2")] [InlineData("9999999999999999999")]
    public void Saved_list_query_errors_offer_recovery_without_requesting_an_invalid_page(string value)
    {
        using var context = SavedContext(out var api);
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/saved?page=" + value);
        var page = context.Render<SavedLists>();
        Assert.Equal(0, api.Reads);
        Assert.Contains("Choose a page from 1–1000", page.Find("[role=alert]").TextContent);
        Assert.Equal("/saved?page=1", page.Find("[role=alert] a").GetAttribute("href"));
        nav.NavigateTo("/saved?page=1000");
        page.WaitForAssertion(() => Assert.Contains("Page 1000", page.Markup));
        Assert.Equal(1, api.Reads);
    }

    [Fact]
    public void Saved_lists_offer_sign_in_recovery_and_can_retry_after_the_session_recovers()
    {
        using var context = SavedContext(out var api);
        api.ReadFailure = new SignInRequiredException();
        var page = context.Render<SavedLists>();
        page.WaitForAssertion(() => Assert.Contains("sign in", page.Find("[role=alert]").TextContent));
        Assert.Equal("/account/sign-in", page.Find("[role=alert] a").GetAttribute("href"));
        api.ReadFailure = null;
        page.Find("[role=alert] button").Click();
        page.WaitForAssertion(() => Assert.Contains("No saved lists", page.Markup));
        Assert.Empty(page.FindAll("[role=alert]"));
    }

    [Fact]
    public async Task A_saved_list_response_from_an_old_page_cannot_replace_the_new_page()
    {
        using var context = SavedContext(out var api);
        var pending = new TaskCompletionSource<SavedListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.PendingRead = pending.Task;
        var page = context.Render<SavedLists>();
        page.WaitForAssertion(() => Assert.Equal(1, api.Reads));
        api.PendingRead = null;
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/saved?page=2");
        page.WaitForAssertion(() => Assert.Contains("Page 2", page.Markup));
        pending.SetResult(new([new(Guid.NewGuid(), "Old shortcut", true, false, false)], 1, false));
        await page.InvokeAsync(async () => await Task.Yield());
        Assert.DoesNotContain("Old shortcut", page.Markup);
    }

    private static BunitContext SavedContext(out Fake api)
    {
        var context = new BunitContext();
        api = new();
        context.Services.AddSingleton<IDashboardApi>(api);
        context.Services.AddSingleton<IHostPresentation>(new Host());
        return context;
    }
}

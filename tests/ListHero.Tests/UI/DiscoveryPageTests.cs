using Bunit;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Discovery;
using ListHero.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.UI;

public sealed class DiscoveryPageTests
{
    [Fact]
    public void Lists_and_people_have_separate_results_paging_and_safe_view_links()
    {
        using var context = Context(out var api);
        var page = context.Render<Discover>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".public-list-card")));
        Assert.Equal($"/view/{api.Id}", page.Find(".public-list-card h2 a").GetAttribute("href"));
        Assert.DoesNotContain("fulfilled", page.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Next", page.Find(".discovery-pagination").TextContent);
        page.Find("#discovery-query").Change("mugs & books");
        page.Find("form").Submit();
        Assert.Contains("q=mugs%20%26%20books", context.Services.GetRequiredService<NavigationManager>().Uri);
        Navigate(context, "/discover?tab=people&q=jane&page=2");
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".person-card")));
        Assert.Equal("/people/jane", page.Find(".person-card").GetAttribute("href"));
        Assert.Contains("Previous", page.Find(".discovery-pagination").TextContent);
        Navigate(context, "/discover?tab=people&q=&page=1");
        page.WaitForAssertion(() => Assert.Contains("Search for a name", page.Markup));
    }

    [Fact]
    public void Empty_results_errors_and_retry_are_visible()
    {
        using var context = Context(out var api); api.Empty = true;
        var page = context.Render<Discover>();
        page.WaitForAssertion(() => Assert.Contains("No public lists found", page.Markup));
        Navigate(context, "/discover?tab=people&q=none");
        page.WaitForAssertion(() => Assert.Contains("No people found", page.Markup));
        api.Failure = new HttpRequestException();
        Navigate(context, "/discover?tab=people&q=error");
        page.WaitForAssertion(() => Assert.Contains("try again", page.Find("[role=alert]").TextContent));
        api.Failure = new ApiRequestException("Unavailable test");
        page.Find("[role=alert] button").Click();
        page.WaitForAssertion(() => Assert.Contains("Unavailable test", page.Find("[role=alert]").TextContent));
        api.Failure = null;
        page.Find("[role=alert] button").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[role=alert]")));
    }

    [Fact]
    public async Task A_late_old_response_cannot_replace_a_new_search()
    {
        using var context = Context(out var api);
        var old = new TaskCompletionSource<DiscoveryPage<PublicListSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Pending = old.Task;
        var page = context.Render<Discover>();
        Assert.Contains("Searching", page.Markup);
        api.Pending = null;
        Navigate(context, "/discover?q=new%20search");
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".public-list-card")));
        old.SetResult(new([], 1, 20, false));
        await page.InvokeAsync(async () => await Task.Yield());
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".public-list-card")));
    }

    [Fact]
    public void Public_person_paging_unavailability_and_empty_pages_do_not_expose_private_data()
    {
        using var context = Context(out var api);
        Navigate(context, "/people/jane?page=2");
        var page = context.Render<PublicPerson>(p => p.Add(c => c.Handle, "jane"));
        page.WaitForAssertion(() => Assert.Equal("Jane", page.Find("h1").TextContent));
        Assert.Contains("Previous", page.Markup); Assert.Contains("Next", page.Markup);
        api.Empty = true;
        Navigate(context, "/people/jane?page=3");
        page.WaitForAssertion(() => Assert.Contains("No lists on this page", page.Markup));
        api.Failure = new ApiRequestException("This public profile is unavailable.");
        page.Render(p => p.Add(c => c.Handle, "hidden"));
        page.WaitForAssertion(() => Assert.Contains("unavailable", page.Find("[role=alert]").TextContent));
        api.Failure = null;
        page.Find("[role=alert] button").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[role=alert]")));
    }

    [Fact]
    public void Profile_setup_requires_consent_locks_handle_and_can_reload_a_conflicting_draft()
    {
        using var context = Context(out var api);
        var page = context.Render<PublicProfile>();
        page.WaitForAssertion(() => Assert.Equal("Suggested name", page.Find("#public-name").GetAttribute("value")));
        page.Find("#public-name").Change("Jane"); page.Find("#public-handle").Change("jane");
        page.Find("form").Submit(); Assert.Null(api.Saved);
        page.Find("input[type=checkbox]").Change(true);
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("saved", page.Find("[role=status]").TextContent));
        Assert.Equal("jane", api.Saved!.Handle); Assert.True(page.Find("#public-handle").HasAttribute("readonly"));
        api.Failure = new ApiConflictException();
        page.Find("#public-name").Change("Unsaved name"); page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("record changed", page.Find("[role=alert]").TextContent));
        api.Failure = null;
        page.FindAll("button").Single(b => b.TextContent == "Reload saved profile").Click();
        page.WaitForAssertion(() => Assert.Equal("Jane", page.Find("#public-name").GetAttribute("value")));
    }

    [Fact]
    public void Profile_load_errors_offer_retry()
    {
        using var context = Context(out var api); api.Failure = new SignInRequiredException();
        var page = context.Render<PublicProfile>();
        page.WaitForAssertion(() => Assert.Contains("sign in", page.Find("[role=alert]").TextContent));
        api.Failure = null; page.FindAll("button").Single(b => b.TextContent == "Try again").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#public-name")));
    }

    private static BunitContext Context(out FakeApi api)
    {
        var context = new BunitContext(); api = new();
        context.Services.AddSingleton<IDiscoveryApi>(api); context.Services.AddSingleton<IPublicProfileApi>(api);
        context.Services.AddSingleton<ListHero.Client.Abstractions.IHostPresentation>(new Presentation());
        return context;
    }
    private static void Navigate(BunitContext context, string uri) => context.Renderer.Dispatcher
        .InvokeAsync(() => context.Services.GetRequiredService<NavigationManager>().NavigateTo(uri)).GetAwaiter().GetResult();
    private sealed class Presentation : ListHero.Client.Abstractions.IHostPresentation
    { public bool IsDevelopment => false; public bool CanSignIn => true; public string SignInUrl => "/account/sign-in"; public string SignOutUrl => "/account/sign-out"; }
    private sealed class FakeApi : IDiscoveryApi, IPublicProfileApi
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool Empty { get; set; }
        public Exception? Failure { get; set; }
        public UpdatePublicProfileRequest? Saved { get; private set; }
        public Task<DiscoveryPage<PublicListSummary>>? Pending { get; set; }
        private OwnProfileResponse current = new(null, null, "Suggested name", "AQAAAAAAAAA=");
        private DiscoveryPage<PublicListSummary> Lists => new(Empty ? [] : [new(Id, "Birthday", "Wishes", DateTimeOffset.UtcNow, new("Jane", "jane"))], 1, 20, !Empty);
        public Task<DiscoveryPage<PublicListSummary>> GetListsAsync(DiscoveryQuery query, CancellationToken ct = default)
            => Failure is not null ? Task.FromException<DiscoveryPage<PublicListSummary>>(Failure) : Pending ?? Task.FromResult(Lists);
        public Task<DiscoveryPage<PublicPersonSummary>> GetPeopleAsync(DiscoveryQuery query, CancellationToken ct = default)
            => Failure is not null ? Task.FromException<DiscoveryPage<PublicPersonSummary>>(Failure) : Task.FromResult(new DiscoveryPage<PublicPersonSummary>(Empty || string.IsNullOrEmpty(query.Query) ? [] : [new("Jane", "jane")], query.Page, 20, false));
        public Task<PublicProfileResponse> GetProfileAsync(string handle, int page = 1, CancellationToken ct = default)
            => Failure is not null ? Task.FromException<PublicProfileResponse>(Failure) : Task.FromResult(new PublicProfileResponse(new("Jane", handle), Lists));
        public Task<OwnProfileResponse> GetAsync(CancellationToken ct = default)
            => Failure is not null ? Task.FromException<OwnProfileResponse>(Failure) : Task.FromResult(current);
        public Task<OwnProfileResponse> UpdateAsync(UpdatePublicProfileRequest request, CancellationToken ct = default)
        {
            if (Failure is not null) return Task.FromException<OwnProfileResponse>(Failure);
            Saved = request; current = new(request.DisplayName, request.Handle, "Suggested name", "AgAAAAAAAAA="); return Task.FromResult(current);
        }
    }
}

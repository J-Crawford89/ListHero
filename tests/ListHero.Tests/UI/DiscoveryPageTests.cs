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
    public void Profile_setup_requires_consent_locks_handle_and_confirms_discard_before_reload()
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
        Assert.Equal("Unsaved name", page.Find("#public-name").GetAttribute("value"));
        page.FindAll("button").Single(b => b.TextContent == "Keep editing").Click();
        Assert.Empty(page.FindAll("section[aria-label='Discard profile changes']"));
        page.FindAll("button").Single(b => b.TextContent == "Reload saved profile").Click();
        page.FindAll("button").Single(b => b.TextContent == "Discard changes and reload").Click();
        page.WaitForAssertion(() => Assert.Equal("Jane", page.Find("#public-name").GetAttribute("value")));
        Assert.Empty(page.FindAll("section[aria-label='Profile conflict']"));
        page.FindAll("button").Single(b => b.TextContent == "Reload saved profile").Click();
        Assert.Empty(page.FindAll("section[aria-label='Discard profile changes']"));
    }

    [Fact]
    public void Profile_conflict_can_compare_latest_and_explicitly_rebase_without_losing_the_draft()
    {
        using var context = Context(out var api);
        api.Current = new("Jane", "jane", "Suggested name", "AQAAAAAAAAA=");
        var page = context.Render<PublicProfile>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#public-name")));
        page.Find("#public-name").Change("My draft name");
        api.Current = api.Current with { DisplayName = "Other session name", RowVersion = "AgAAAAAAAAA=" };
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("record changed", page.Find("[role=alert]").TextContent));
        Assert.True(page.Find("button[type=submit]").HasAttribute("disabled"));
        api.Failure = new HttpRequestException();
        page.FindAll("button").Single(b => b.TextContent == "Load latest version").Click();
        page.WaitForAssertion(() => Assert.Contains("try again", page.Find("[role=alert]").TextContent));
        Assert.Equal("My draft name", page.Find("#public-name").GetAttribute("value"));
        api.Failure = null;
        page.FindAll("button").Single(b => b.TextContent == "Load latest version").Click();
        page.WaitForAssertion(() => Assert.Contains("Other session name", page.Find("section[aria-label='Profile conflict']").TextContent));
        Assert.Equal("My draft name", page.Find("#public-name").GetAttribute("value"));
        Assert.Null(api.Saved);
        page.FindAll("button").Single(b => b.TextContent == "Use latest version and keep my draft").Click();
        Assert.Equal("My draft name", page.Find("#public-name").GetAttribute("value"));
        Assert.False(page.Find("button[type=submit]").HasAttribute("disabled"));
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Equal("My draft name", api.Saved?.DisplayName));
        Assert.Equal("AgAAAAAAAAA=", api.Saved!.RowVersion);
        Assert.Contains("profile is saved", page.Find("[role=status]").TextContent);
    }

    [Fact]
    public void Profile_setup_conflict_preserves_name_but_uses_the_handle_already_claimed_in_another_session()
    {
        using var context = Context(out var api);
        var page = context.Render<PublicProfile>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#public-name")));
        page.Find("#public-name").Change("My name");
        page.Find("#public-handle").Change("my-handle");
        page.Find("input[type=checkbox]").Change(true);
        api.Current = new("Other name", "saved-handle", "Suggested name", "AgAAAAAAAAA=");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("section[aria-label='Profile conflict']")));
        page.FindAll("button").Single(b => b.TextContent == "Load latest version").Click();
        page.WaitForAssertion(() => Assert.Contains("handle was set in another session", page.Markup));
        Assert.Equal("my-handle", page.Find("#public-handle").GetAttribute("value"));
        page.FindAll("button").Single(b => b.TextContent == "Use latest version and keep my draft").Click();
        Assert.Equal("My name", page.Find("#public-name").GetAttribute("value"));
        Assert.Equal("saved-handle", page.Find("#public-handle").GetAttribute("value"));
        Assert.True(page.Find("#public-handle").HasAttribute("readonly"));
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Equal("saved-handle", api.Saved?.Handle));
    }

    [Fact]
    public void Profile_conflict_can_discard_the_draft_and_use_the_compared_saved_version()
    {
        using var context = Context(out var api);
        var page = context.Render<PublicProfile>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#public-name")));
        page.Find("#public-name").Change("Draft");
        page.Find("#public-handle").Change("draft");
        page.Find("input[type=checkbox]").Change(true);
        api.Current = api.Current with { SuggestedDisplayName = "Refreshed suggestion", RowVersion = "AgAAAAAAAAA=" };
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("section[aria-label='Profile conflict']")));
        page.FindAll("button").Single(b => b.TextContent == "Load latest version").Click();
        page.WaitForAssertion(() => Assert.Contains("Not set", page.Markup));
        page.FindAll("button").Single(b => b.TextContent == "Discard my draft and use saved profile").Click();
        Assert.Equal("Refreshed suggestion", page.Find("#public-name").GetAttribute("value"));
        Assert.Empty(page.FindAll("section[aria-label='Profile conflict']"));
        Assert.False(page.Find("#public-handle").HasAttribute("readonly"));
        Assert.Null(api.Saved);
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
        public OwnProfileResponse Current { get; set; } = new(null, null, "Suggested name", "AQAAAAAAAAA=");
        private DiscoveryPage<PublicListSummary> Lists => new(Empty ? [] : [new(Id, "Birthday", "Wishes", DateTimeOffset.UtcNow, new("Jane", "jane"))], 1, 20, !Empty);
        public Task<DiscoveryPage<PublicListSummary>> GetListsAsync(DiscoveryQuery query, CancellationToken ct = default)
            => Failure is not null ? Task.FromException<DiscoveryPage<PublicListSummary>>(Failure) : Pending ?? Task.FromResult(Lists);
        public Task<DiscoveryPage<PublicPersonSummary>> GetPeopleAsync(DiscoveryQuery query, CancellationToken ct = default)
            => Failure is not null ? Task.FromException<DiscoveryPage<PublicPersonSummary>>(Failure) : Task.FromResult(new DiscoveryPage<PublicPersonSummary>(Empty || string.IsNullOrEmpty(query.Query) ? [] : [new("Jane", "jane")], query.Page, 20, false));
        public Task<PublicProfileResponse> GetProfileAsync(string handle, int page = 1, CancellationToken ct = default)
            => Failure is not null ? Task.FromException<PublicProfileResponse>(Failure) : Task.FromResult(new PublicProfileResponse(new("Jane", handle), Lists));
        public Task<OwnProfileResponse> GetAsync(CancellationToken ct = default)
            => Failure is not null ? Task.FromException<OwnProfileResponse>(Failure) : Task.FromResult(Current);
        public Task<OwnProfileResponse> UpdateAsync(UpdatePublicProfileRequest request, CancellationToken ct = default)
        {
            if (Failure is not null) return Task.FromException<OwnProfileResponse>(Failure);
            if (request.RowVersion != Current.RowVersion) return Task.FromException<OwnProfileResponse>(new ApiConflictException());
            Saved = request; Current = new(request.DisplayName, request.Handle, "Suggested name", Convert.ToBase64String(Guid.NewGuid().ToByteArray())); return Task.FromResult(Current);
        }
    }
}

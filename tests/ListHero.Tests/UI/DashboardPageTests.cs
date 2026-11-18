using Bunit;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Lists;
using ListHero.UI.Pages;
using ListHero.UI.Components;
using Microsoft.Extensions.DependencyInjection;
namespace ListHero.Tests.UI;
public sealed class DashboardPageTests
{
    [Fact]
    public void Dashboard_saves_owned_pins_and_saved_page_can_unpin_and_remove()
    {
        using var context = new BunitContext(); context.Services.AddSingleton<ListHero.Client.Abstractions.IHostPresentation>(new Host()); var api = new Fake(); context.Services.AddSingleton<IDashboardApi>(api);
        var page = context.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains("My wishes", page.Markup));
        page.FindAll("button").Single(b => b.TextContent == "Pin to dashboard").Click();
        page.WaitForAssertion(() => Assert.True(api.Pinned));
        var saved = context.Render<SavedLists>();
        saved.WaitForAssertion(() => Assert.Contains("My wishes", saved.Markup));
        saved.FindAll("button").Single(b => b.TextContent == "Unpin").Click();
        saved.WaitForAssertion(() => Assert.False(api.Pinned));
        saved.FindAll("button").Single(b => b.TextContent == "Remove shortcut").Click();
        saved.WaitForAssertion(() => Assert.Contains("No saved lists", saved.Markup));
        api.Empty = true; var empty = context.Render<Dashboard>();
        empty.WaitForAssertion(() => Assert.Contains("Your first wish", empty.Markup));
    }
    [Fact]
    public void Signed_in_viewer_can_save_a_validated_link()
    {
        using var context = new BunitContext(); context.Services.AddSingleton<ListHero.Client.Abstractions.IHostPresentation>(new Host()); var api = new Fake(); context.Services.AddSingleton<IDashboardApi>(api);
        context.AddAuthorization().SetAuthorized("Buyer");
        var page = context.Render<SaveListButton>(p => p.Add(c => c.ListId, Guid.NewGuid()).Add(c => c.ShareToken, "opaque-link"));
        page.Find("button").Click(); page.WaitForAssertion(() => Assert.Contains("Saved", page.Markup));
        Assert.Equal("opaque-link", api.Token);
    }
    private sealed class Host : ListHero.Client.Abstractions.IHostPresentation { public bool IsDevelopment => false; public bool CanSignIn => true; public string SignInUrl => "/account/sign-in"; public string SignOutUrl => "/account/sign-out"; }
    internal sealed class Fake : IDashboardApi
    {
        private readonly Guid id = Guid.NewGuid(); public bool Pinned; public bool Empty; private bool saved; public string? Token;
        private SavedListSummary Entry => new(id, "My wishes", true, true, Pinned);
        public Task<DashboardResponse> GetAsync(CancellationToken ct = default) => Task.FromResult(new DashboardResponse(Pinned ? [Entry] : [], Empty ? [] : [new(id,"My wishes","",false,DateTimeOffset.UtcNow)], saved ? [Entry] : []));
        public Task<SavedListPage> SavedAsync(int page = 1, CancellationToken ct = default) => Task.FromResult(new SavedListPage(saved ? [Entry] : [],page,false));
        public Task SaveAsync(Guid listId, bool pinned, string? shareToken = null, CancellationToken ct = default) { saved=true; Pinned=pinned; Token=shareToken; return Task.CompletedTask; }
        public Task RemoveAsync(Guid listId, CancellationToken ct = default) { saved=false; return Task.CompletedTask; }
    }
}

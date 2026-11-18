using Bunit;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Lists;
using ListHero.UI.Pages;
using ListHero.UI.Components;
using Microsoft.Extensions.DependencyInjection;
namespace ListHero.Tests.UI;
public sealed partial class DashboardPageTests
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
        var id = Guid.NewGuid();
        api.Pinned = true;
        var page = context.Render<SaveListButton>(p => p.Add(c => c.ListId, id).Add(c => c.ShareToken, "opaque-link"));
        page.Find("button").Click(); page.WaitForAssertion(() => Assert.Contains("Saved", page.Markup));
        Assert.Equal("opaque-link", api.Token);
        Assert.Null(api.RequestedPin);
        Assert.True(api.Pinned);
        page.Render(p => p.Add(c => c.ListId, id).Add(c => c.ShareToken, "opaque-link"));
        Assert.Single(page.FindAll("a[href='/saved']"));
        page.Render(p => p.Add(c => c.ShareToken, "replacement-link"));
        Assert.Empty(page.FindAll("a[href='/saved']"));
        page.Find("button").Click();
        page.WaitForAssertion(() => Assert.Equal("replacement-link", api.Token));
        page.Render(p => p.Add(c => c.ListId, Guid.NewGuid()));
        Assert.Empty(page.FindAll("a[href='/saved']"));
        page.Render(p => p.Add(c => c.Pin, true));
        page.Find("button").Click();
        page.WaitForAssertion(() => Assert.True(api.RequestedPin));
    }

    [Fact]
    public async Task Late_save_confirmation_does_not_apply_to_a_different_list()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ListHero.Client.Abstractions.IHostPresentation>(new Host());
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new Fake { PendingSave = pending.Task };
        context.Services.AddSingleton<IDashboardApi>(api);
        context.AddAuthorization().SetAuthorized("Buyer");
        var page = context.Render<SaveListButton>(p => p.Add(c => c.ListId, Guid.NewGuid()));
        var saving = page.Find("button").ClickAsync(new());
        page.Render(p => p.Add(c => c.ListId, Guid.NewGuid()));
        pending.SetResult();
        await saving;
        page.WaitForAssertion(() => Assert.False(page.Find("button").HasAttribute("disabled")));
        Assert.Empty(page.FindAll("a[href='/saved']"));
    }
    private sealed class Host : ListHero.Client.Abstractions.IHostPresentation { public bool IsDevelopment => false; public bool CanSignIn => true; public string SignInUrl => "/account/sign-in"; public string SignOutUrl => "/account/sign-out"; }
    internal sealed class Fake : IDashboardApi
    {
        private readonly Guid id = Guid.NewGuid(); public bool Pinned; public bool Empty; private bool saved; public string? Token;
        public bool? RequestedPin;
        public Task? PendingSave;
        public Exception? ReadFailure;
        public Task<SavedListPage>? PendingRead;
        public int Reads;
        private SavedListSummary Entry => new(id, "My wishes", true, true, Pinned);
        public Task<DashboardResponse> GetAsync(CancellationToken ct = default) => Task.FromResult(new DashboardResponse(Pinned ? [Entry] : [], Empty ? [] : [new(id,"My wishes","",false,DateTimeOffset.UtcNow)], saved ? [Entry] : []));
        public Task<SavedListPage> SavedAsync(int page = 1, CancellationToken ct = default)
        {
            Reads++;
            return ReadFailure is not null ? Task.FromException<SavedListPage>(ReadFailure)
                : PendingRead ?? Task.FromResult(new SavedListPage(saved ? [Entry] : [],page,false));
        }
        public async Task SaveAsync(Guid listId, bool? pinned, string? shareToken = null, CancellationToken ct = default)
        {
            if (PendingSave is not null) await PendingSave;
            saved = true;
            RequestedPin = pinned;
            Pinned = pinned ?? Pinned;
            Token = shareToken;
        }
        public Task RemoveAsync(Guid listId, CancellationToken ct = default) { saved=false; return Task.CompletedTask; }
    }
}

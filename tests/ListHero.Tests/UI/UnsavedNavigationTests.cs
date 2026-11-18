using Bunit;
using ListHero.Client.Abstractions;
using ListHero.Client.Services;
using ListHero.UI.Components;
using ListHero.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace ListHero.Tests.UI;

public sealed class UnsavedNavigationTests
{
    [Fact]
    public async Task Navigation_can_be_cancelled_and_session_recovery_stays_visible_until_reload()
    {
        using var context = new BunitContext();
        var js = new GuardRuntime();
        var session = new ClientSessionState();
        context.Services.AddSingleton<IJSRuntime>(js);
        context.Services.AddSingleton<IClientSessionState>(session);
        var page = context.Render<UnsavedNavigationGuard>();
        page.WaitForAssertion(() => Assert.True(js.Started));
        var nav = context.Services.GetRequiredService<NavigationManager>();
        var original = nav.Uri;
        nav.NavigateTo("/saved");
        page.WaitForAssertion(() => Assert.Equal(1, js.Prompts));
        Assert.Equal(original, nav.Uri);
        js.Allow = true;
        nav.NavigateTo("/dashboard");
        page.WaitForAssertion(() => Assert.EndsWith("/dashboard", nav.Uri));
        await page.Instance.SessionChanged();
        Assert.True(session.RequiresSignIn);
        Assert.Contains("Copy any unsaved work", page.Find("#session-recovery").TextContent);
        page.Find("button").Click();
        Assert.Equal(1, js.Reloads);
        Assert.True(session.RequiresSignIn);
        await page.Instance.DisposeAsync();
        Assert.True(js.Stopped);
    }

    [Fact]
    public void Guard_initialization_failure_has_a_clear_fallback()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IJSRuntime>(new GuardRuntime { FailImport = true });
        context.Services.AddSingleton<IClientSessionState>(new ClientSessionState());
        var page = context.Render<UnsavedNavigationGuard>();
        page.WaitForAssertion(() => Assert.Contains("Save your work before leaving", page.Find("[role=alert]").TextContent));
    }

    private sealed class GuardRuntime : IJSRuntime, IJSObjectReference
    {
        public bool Started, Stopped, Allow, FailImport;
        public int Prompts, Reloads;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import" && FailImport) throw new JSException("Module failed");
            if (identifier == "watch") Started = true;
            if (identifier == "confirmDiscard") { Prompts++; return ValueTask.FromResult((TValue)(object)Allow); }
            if (identifier == "reloadWithConfirmation") { Reloads++; return ValueTask.FromResult(default(TValue)!); }
            if (identifier == "dispose") { Stopped = true; return ValueTask.FromResult(default(TValue)!); }
            Assert.Contains(identifier, new[] { "import", "watch" });
            return ValueTask.FromResult((TValue)(object)this);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed partial class OwnerPageTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Switching_item_editors_requires_permission_to_discard_the_current_draft(bool allow)
    {
        using var context = CreateContext(out var api, allow);
        Seed(api);
        var first = api.Current!.Items[0];
        api.Current = api.Current with { Items = [first, first with { Id = Guid.NewGuid(), Name = "Second mug" }] };
        var page = context.Render<OwnerList>(p => p.Add(c => c.ListId, api.ListId));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".edit-item").Count));
        page.Find("#item-name").Change("Unfinished new wish");
        page.FindAll(".edit-item")[0].Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#edit-item-form")));
        page.Find("#edit-item-name").Change("Unfinished edit");
        page.FindAll(".edit-item")[0].Click();
        Assert.Equal("Unfinished edit", page.Find("#edit-item-name").GetAttribute("value"));
        page.FindAll(".edit-item")[1].Click();
        page.WaitForAssertion(() => Assert.Equal(allow ? "Second mug" : "Unfinished edit", page.Find("#edit-item-name").GetAttribute("value")));
        Assert.Equal("Unfinished new wish", page.Find("#item-name").GetAttribute("value"));
        Assert.True(page.Find("#add-item-form").ParentElement!.HasAttribute("hidden"));
    }
}

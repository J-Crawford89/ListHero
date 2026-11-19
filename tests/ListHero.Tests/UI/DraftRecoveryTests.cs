using System.Text.Json;
using Bunit;
using ListHero.Client.Abstractions;
using ListHero.Client.Services;
using ListHero.Contracts.Lists;
using ListHero.UI.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace ListHero.Tests.UI;

public sealed class DraftRecoveryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DraftField[] Fields = [new("name", "Unblurred private text", false, "text")];
    private static (BunitContext Context, Store Store, Runtime Runtime) Setup(bool saved = true)
    {
        var context = new BunitContext(); var store = new Store(); var js = new Runtime();
        if (saved) store.Content = JsonSerializer.Serialize(new RecoveredDraft<UpdateWishListRequest>(new() { Name = "Saved draft", RowVersion = "old-version" }, "entity", Fields), Json);
        context.Services.AddSingleton<IDraftRecoveryStore>(store); context.Services.AddSingleton<IJSRuntime>(js);
        return (context, store, js);
    }
    private static IRenderedComponent<DraftRecovery<UpdateWishListRequest>> Render(BunitContext context, Action<RecoveredDraft<UpdateWishListRequest>>? restore = null)
        => context.Render<DraftRecovery<UpdateWishListRequest>>(p => p.Add(c => c.Context, "synthetic-form").Add(c => c.FormId, "form")
            .Add(c => c.Label, "list details").Add(c => c.Model, new UpdateWishListRequest { Name = "Current", RowVersion = "current-version" })
            .Add(c => c.Restored, value => restore?.Invoke(value)));

    [Fact]
    public async Task Recovery_requires_a_choice_preserves_versions_and_never_submits_the_draft()
    {
        var setup = Setup(); using var context = setup.Context; RecoveredDraft<UpdateWishListRequest>? restored = null;
        var page = Render(context, value => restored = value);
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".draft-recovery")));
        Assert.Null(restored); Assert.Equal(0, setup.Store.Writes);
        page.FindAll("button").Single(button => button.TextContent == "Restore draft").Click();
        page.WaitForAssertion(() => Assert.Equal(1, setup.Runtime.Restores));
        Assert.Equal("old-version", restored!.Model.RowVersion); Assert.Equal("entity", restored.EntityId);
        Assert.Equal(Fields, setup.Runtime.Fields); Assert.Equal(0, setup.Store.Writes);
        Assert.Contains("nothing has been submitted", page.Markup);
        await page.Instance.DisposeAsync(); Assert.True(setup.Runtime.Disposed);
    }

    [Fact]
    public async Task Saves_include_raw_fields_and_clearing_invalidates_old_callbacks()
    {
        var setup = Setup(false); using var context = setup.Context; var page = Render(context);
        page.WaitForAssertion(() => Assert.True(setup.Runtime.Watching));
        await page.Instance.DraftChanged(Fields, 0);
        var stored = JsonSerializer.Deserialize<RecoveredDraft<UpdateWishListRequest>>(setup.Store.Content!, Json)!;
        Assert.Equal("current-version", stored.Model.RowVersion); Assert.Equal(Fields, stored.Fields);
        Assert.Contains("Recovery draft saved", page.Markup);
        await page.Instance.ClearAsync(); Assert.Null(setup.Store.Content);
        await page.Instance.DraftChanged(Fields, 0); Assert.Equal(1, setup.Store.Writes);
        await page.Instance.DraftChanged(Fields, 1); Assert.Equal(2, setup.Store.Writes);
        await page.Instance.DraftChanged(Enumerable.Repeat(Fields[0], 33).ToArray(), 1); Assert.Equal(2, setup.Store.Writes);
    }

    [Theory]
    [InlineData("{")] [InlineData("null")] [InlineData("{\"model\":null,\"fields\":[]}")]
    public void Invalid_saved_shapes_are_cleared_without_rendering_private_data(string content)
    {
        var setup = Setup(); using var context = setup.Context; setup.Store.Content = content;
        var page = Render(context);
        page.WaitForAssertion(() => Assert.Equal(1, setup.Store.Removals)); Assert.Empty(page.FindAll(".draft-recovery"));
    }

    [Theory]
    [InlineData("session")] [InlineData("declined")] [InlineData("missing-item")] [InlineData("missing-form")]
    public void Restoration_is_blocked_by_stale_sessions_declined_replacement_or_unavailable_entities(string issue)
    {
        var setup = Setup(); using var context = setup.Context;
        if (issue == "session") { var session = new ClientSessionState(); session.RequireSignIn(); context.Services.AddSingleton<IClientSessionState>(session); }
        setup.Runtime.Allow = issue != "declined"; setup.Runtime.FormPresent = issue != "missing-form";
        var page = Render(context, value => { if (issue == "missing-item") value.RestoreAllowed = false; });
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".draft-recovery")));
        page.FindAll("button").Single(button => button.TextContent == "Restore draft").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".draft-recovery")));
        Assert.Equal(issue == "missing-form" ? 1 : 0, setup.Runtime.Restores);
        Assert.NotNull(setup.Store.Content);
    }

    [Fact]
    public async Task An_older_save_completion_does_not_claim_that_newer_pending_edits_are_saved()
    {
        var setup = Setup(false); using var context = setup.Context; var page = Render(context);
        page.WaitForAssertion(() => Assert.True(setup.Runtime.Watching));
        await page.Instance.DraftPending(0, 2);
        await page.Instance.DraftChanged(Fields, 0, 1);
        Assert.Contains("Saving recovery draft", page.Markup); Assert.DoesNotContain("Recovery draft saved", page.Markup);
        await page.Instance.DraftChanged(Fields, 0, 2);
        Assert.Contains("Recovery draft saved", page.Markup);
    }

    [Fact]
    public async Task Restoring_after_newer_edits_resets_save_status_for_the_recovered_form()
    {
        var setup = Setup(); using var context = setup.Context; var page = Render(context);
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".draft-recovery")));
        await page.Instance.DraftPending(0, 100);
        await page.Instance.DraftChanged(Fields, 0, 100);
        page.FindAll("button").Single(button => button.TextContent == "Restore draft").Click();
        page.WaitForAssertion(() => Assert.Equal(1, setup.Runtime.Restores));
        await page.Instance.DraftPending(1, 1);
        await page.Instance.DraftChanged(Fields, 1, 1);
        Assert.Contains("Recovery draft saved", page.Markup);
    }

    [Fact]
    public void A_restore_interop_failure_keeps_the_recovery_choice_and_shows_a_fallback()
    {
        var setup = Setup(); using var context = setup.Context; setup.Runtime.FailRestore = true;
        var page = Render(context); page.WaitForAssertion(() => Assert.Single(page.FindAll(".draft-recovery")));
        page.FindAll("button").Single(button => button.TextContent == "Restore draft").Click();
        page.WaitForAssertion(() => Assert.Contains("Draft recovery is unavailable", page.Markup));
        Assert.Single(page.FindAll(".draft-recovery"));
    }

    [Fact]
    public void Explicit_discard_removes_the_stored_copy()
    {
        var setup = Setup(); using var context = setup.Context; var page = Render(context);
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".draft-recovery")));
        page.FindAll("button").Single(button => button.TextContent == "Discard draft").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".draft-recovery")));
        Assert.Null(setup.Store.Content); Assert.Equal(1, setup.Store.Removals);
    }

    [Theory]
    [InlineData("import")] [InlineData("read")] [InlineData("write")] [InlineData("remove")]
    public async Task Storage_and_initialization_failures_show_a_safe_fallback(string issue)
    {
        var setup = Setup(false); using var context = setup.Context;
        setup.Runtime.FailImport = issue == "import"; setup.Store.FailRead = issue == "read";
        var page = Render(context);
        if (issue is "write" or "remove")
        {
            page.WaitForAssertion(() => Assert.True(setup.Runtime.Watching)); setup.Store.FailWrite = issue == "write"; setup.Store.FailRemove = issue == "remove";
            if (issue == "write") await page.Instance.DraftChanged(Fields, 0); else await page.Instance.ClearAsync();
        }
        page.WaitForAssertion(() => Assert.Contains("Draft recovery is unavailable", page.Markup));
        Assert.DoesNotContain("private-runtime-error", page.Markup);
    }

    private sealed class Store : IDraftRecoveryStore
    {
        public string? Content; public int Writes, Removals; public bool FailRead, FailWrite, FailRemove;
        public ValueTask<string?> ReadAsync(string context, bool allowAnonymous) => FailRead ? throw new DraftStorageException() : ValueTask.FromResult(Content);
        public ValueTask WriteAsync(string context, string content, bool allowAnonymous) { if (FailWrite) throw new DraftStorageException(); Content = content; Writes++; return ValueTask.CompletedTask; }
        public ValueTask RemoveAsync(string context, bool allowAnonymous) { if (FailRemove) throw new DraftStorageException(); Content = null; Removals++; return ValueTask.CompletedTask; }
    }
    private sealed class Runtime : IJSRuntime, IJSObjectReference
    {
        public bool Watching, Disposed, FailImport, FailRestore, Allow = true, FormPresent = true; public int Restores;
        public DraftField[]? Fields;
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => InvokeAsync<T>(name, default, args);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args)
        {
            if (name == "import" && FailImport) throw new JSException("private-runtime-error");
            object? result = null;
            if (name is "import" or "watch") { if (name == "watch") Watching = true; result = this; }
            if (name == "confirmRestore") { if (FailRestore) throw new JSException("private-runtime-error"); result = Allow; }
            if (name == "restore") { Restores++; Fields = (DraftField[])args![0]!; result = FormPresent; }
            if (name == "dispose") Disposed = true;
            return ValueTask.FromResult(result is null ? default! : (T)result);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

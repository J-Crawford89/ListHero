using Bunit;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Lists;
using ListHero.Contracts.Products;
using ListHero.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.UI;

public sealed class ProductImportFormTests
{
    [Theory]
    [InlineData("found", 2, "Added the available details")]
    [InlineData("found", 0, "Your existing details were kept")]
    [InlineData("busy", 0, "lookup is busy")]
    [InlineData("unavailable", 0, "couldn't find usable details")]
    public void URL_change_imports_into_empty_fields_and_reports_partial_or_unavailable_details(string status, int count, string message)
    {
        using var context = Context(out var api);
        var module = context.JSInterop.SetupModule("./_content/ListHero.UI/product-import.js");
        module.Setup<int>("applySuggestions", _ => true).SetResult(count);
        api.Read = (_, _) => Task.FromResult(new ProductLinkResult(status, status == "found" ? "Gift" : null));
        var model = new CreateWishListItemRequest(); var page = Form(context, model);
        page.Find("#item-link").Change("https://shop.example/item");
        page.WaitForAssertion(() => Assert.Contains(message, page.Markup));
        Assert.Equal(1, api.Calls);
        Assert.Equal("", model.Name); // DOM applies suggestions; the response never bypasses current-value checks.
    }

    [Theory]
    [InlineData("signin", "Please sign in again")]
    [InlineData("http", "lookup is unavailable")]
    [InlineData("timeout", "lookup took too long")]
    public void Errors_leave_the_draft_intact(string error, string expected)
    {
        using var context = Context(out var api);
        api.Read = (_, _) => throw error switch { "signin" => new SignInRequiredException(), "timeout" => new OperationCanceledException(), _ => new HttpRequestException("private-error") };
        var model = new CreateWishListItemRequest { Name = "My own title", Description = "My text" }; var page = Form(context, model);
        page.Find("#item-link").Change("https://shop.example/item");
        page.WaitForAssertion(() => Assert.Contains(expected, page.Markup));
        Assert.Equal("My own title", model.Name); Assert.Equal("My text", model.Description); Assert.DoesNotContain("private-error", page.Markup);
    }

    [Fact]
    public async Task New_URL_and_disposal_cancel_previous_work_and_ignore_stale_responses()
    {
        using var context = Context(out var api); var pending = new TaskCompletionSource<ProductLinkResult>(); CancellationToken first = default;
        api.Read = (_, ct) => { if (api.Calls == 1) { first = ct; return pending.Task; } return Task.FromResult(new ProductLinkResult("unavailable")); };
        var model = new CreateWishListItemRequest(); var page = Form(context, model);
        var changing = page.Find("#item-link").ChangeAsync(new ChangeEventArgs { Value = "https://shop.example/first" });
        page.WaitForAssertion(() => Assert.Equal(1, api.Calls));
        page.Find("#item-link").Change("https://shop.example/second");
        Assert.True(first.IsCancellationRequested);
        pending.SetResult(new("found", "Stale title")); await changing;
        page.WaitForAssertion(() => Assert.Contains("couldn't find usable details", page.Markup));
        Assert.Empty(context.JSInterop.Invocations);
        page.Find("#item-link").Change("http://invalid.example/item"); Assert.Equal(2, api.Calls);
        Assert.Contains("full HTTPS", page.Markup);
        var waiting = new TaskCompletionSource<ProductLinkResult>(); CancellationToken last = default;
        api.Read = (_, ct) => { last = ct; return waiting.Task; };
        var leaving = page.Find("#item-link").ChangeAsync(new ChangeEventArgs { Value = "https://shop.example/third" });
        page.WaitForAssertion(() => Assert.Equal(3, api.Calls));
        await page.FindComponent<WishItemFields>().Instance.DisposeAsync(); Assert.True(last.IsCancellationRequested);
        waiting.SetResult(new("found", "Too late")); await leaving;
    }

    private static BunitContext Context(out Fake api)
    {
        var context = new BunitContext(); api = new(); context.Services.AddSingleton<IProductLinkApi>(api); return context;
    }
    private static IRenderedComponent<EditForm> Form(BunitContext context, CreateWishListItemRequest model)
        => context.Render<EditForm>(p => p.Add(c => c.Model, model).Add(c => c.ChildContent, (RenderFragment<EditContext>)(_ => builder => { builder.OpenComponent<WishItemFields>(0); builder.AddAttribute(1, "Model", model); builder.CloseComponent(); })));
    private sealed class Fake : IProductLinkApi
    {
        public int Calls; public Func<string, CancellationToken, Task<ProductLinkResult>> Read = (_, _) => Task.FromResult(new ProductLinkResult("unavailable"));
        public Task<ProductLinkResult> ImportAsync(string url, CancellationToken ct = default) { Calls++; return Read(url, ct); }
    }
}

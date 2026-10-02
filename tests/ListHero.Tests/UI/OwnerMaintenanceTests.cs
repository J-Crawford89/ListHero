using Bunit;
using ListHero.Contracts.Lists;
using ListHero.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.UI;

public sealed partial class OwnerPageTests
{
    [Fact]
    public void List_editing_preserves_fields_and_toggles_visibility_then_can_be_cancelled()
    {
        using var context = CreateContext(out var api);
        Seed(api);
        var page = context.Render<OwnerList>(p => p.Add(c => c.ListId, api.ListId));
        page.WaitForAssertion(() => Assert.Equal("Birthday", page.Find("h1").TextContent));
        Button(page, "Edit list").Click();
        page.Find("#edit-list-name").Change("Holiday");
        page.Find("#edit-list-description").Change("For the family");
        page.Find("#edit-list-form input[type=checkbox]").Change(true);
        page.Find("#edit-list-form").Submit();
        page.WaitForAssertion(() => Assert.Equal("Holiday", page.Find("h1").TextContent));
        Assert.Equal("For the family", api.UpdatedList!.Description);
        Assert.True(api.UpdatedList.IsPublic);
        Assert.Equal("AQAAAAAAAAA=", api.UpdatedList.RowVersion);
        Button(page, "Edit list").Click();
        page.Find("#edit-list-form input[type=checkbox]").Change(false);
        page.Find("#edit-list-form").Submit();
        page.WaitForAssertion(() => Assert.Contains("Private", page.Find(".private-badge").TextContent));
        Button(page, "Edit list").Click();
        page.Find("#edit-list-name").Change("Discard this");
        page.FindAll("#edit-list-form button").Single(b => b.TextContent == "Cancel").Click();
        Assert.Empty(page.FindAll("#edit-list-form"));
        Assert.Equal("Holiday", page.Find("h1").TextContent);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Removal_requires_confirmation_and_cancel_never_sends_an_archive(bool wholeList)
    {
        using var context = CreateContext(out var api);
        Seed(api);
        var version = wholeList ? api.Current!.RowVersion : api.Current!.Items[0].RowVersion;
        var page = context.Render<OwnerList>(p => p.Add(c => c.ListId, api.ListId));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".wish-card")));
        var label = wholeList ? "Remove list" : "Remove item";
        Button(page, label).Click();
        Assert.Null(api.ArchivedItem); Assert.Null(api.ArchivedList);
        page.FindAll(".confirmation button").Single(b => b.TextContent == "Cancel").Click();
        Assert.Empty(page.FindAll(".confirmation"));
        Assert.Null(api.ArchivedItem); Assert.Null(api.ArchivedList);
        Button(page, label).Click();
        page.FindAll(".confirmation button").Single(b => b.TextContent == label).Click();
        if (wholeList)
        {
            page.WaitForAssertion(() => Assert.EndsWith("/lists", context.Services.GetRequiredService<NavigationManager>().Uri));
            Assert.Equal(version, api.ArchivedList!.RowVersion);
        }
        else
        {
            page.WaitForAssertion(() => Assert.Empty(page.FindAll(".wish-card")));
            Assert.Equal(version, api.ArchivedItem!.RowVersion);
            Assert.Empty(page.FindAll(".confirmation"));
        }
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Conflicting_draft_requires_explicit_acceptance_of_the_refreshed_version(bool wholeList)
    {
        using var context = CreateContext(out var api);
        Seed(api);
        var page = context.Render<OwnerList>(p => p.Add(c => c.ListId, api.ListId));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".edit-item")));
        if (wholeList) Button(page, "Edit list").Click(); else page.Find(".edit-item").Click();
        var form = wholeList ? "#edit-list-form" : "#edit-item-form";
        var input = wholeList ? "#edit-list-name" : "#edit-item-name";
        page.WaitForAssertion(() => Assert.Single(page.FindAll(form)));
        page.Find(input).Change("Keep my draft");
        api.Current = wholeList ? api.Current! with { Name = "Saved by another tab", RowVersion = "AgAAAAAAAAA=" }
            : api.Current! with { Items = [api.Current.Items[0] with { Name = "Saved by another tab", RowVersion = "AgAAAAAAAAA=" }] };
        page.Find(form).Submit();
        page.WaitForAssertion(() => Assert.Contains("This record changed", page.Find("[role=alert]").TextContent));
        Button(page, "Reload saved list").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(form + " button"), b => b.TextContent == "Use refreshed version and keep my draft"));
        Assert.Equal("Keep my draft", page.Find(input).GetAttribute("value"));
        Assert.Equal("AQAAAAAAAAA=", wholeList ? api.UpdatedList!.RowVersion : api.UpdatedItem!.RowVersion);
        page.FindAll(form + " button").Single(b => b.TextContent == "Use refreshed version and keep my draft").Click();
        page.Find(form).Submit();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(form)));
        Assert.Equal("AgAAAAAAAAA=", wholeList ? api.UpdatedList!.RowVersion : api.UpdatedItem!.RowVersion);
        Assert.Equal("Keep my draft", wholeList ? page.Find("h1").TextContent : page.Find(".wish-card h2").TextContent);
    }

    [Fact]
    public void Item_edit_submits_all_fields_and_cancel_preserves_the_saved_item()
    {
        using var context = CreateContext(out var api);
        Seed(api);
        var page = context.Render<OwnerList>(p => p.Add(c => c.ListId, api.ListId));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".edit-item")));
        page.Find(".edit-item").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#edit-item-form")));
        page.Find("#edit-item-name").Change("New mug");
        page.Find("#edit-item-description").Change("Blue");
        page.Find("#edit-item-price").Change("15.25");
        page.Find("#edit-item-quantity").Change("2");
        page.Find("#edit-item-priority").Change("4");
        page.Find("#edit-item-order").Change("9");
        page.Find("#edit-item-link").Change("https://example.test/mug");
        page.Find("#edit-item-image").Change("https://example.test/mug.png");
        page.Find("#edit-item-form").Submit();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#edit-item-form")));
        var saved = api.UpdatedItem!;
        Assert.Equal("Blue", saved.Description); Assert.Equal(15.25, saved.ApproximateUnitPrice);
        Assert.Equal(2, saved.DesiredQuantity); Assert.Equal(4, saved.Priority); Assert.Equal(9, saved.DisplayOrder);
        Assert.Equal("https://example.test/mug", saved.Url); Assert.Equal("https://example.test/mug.png", saved.ImageUrl);
        page.Find(".edit-item").Click();
        page.Find("#edit-item-name").Change("Discard this");
        page.FindAll("#edit-item-form button").Single(b => b.TextContent == "Cancel").Click();
        Assert.Equal("New mug", page.Find(".wish-card h2").TextContent);
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<OwnerList> page, string label)
        => page.FindAll("button").First(b => b.TextContent == label);
    private static void Seed(TestWishListApi api)
        => api.Current = new(api.ListId, "Birthday", "Some wishes", false,
            [new(Guid.NewGuid(), "Mug", "", 20, 1, 0, 0, null, null, "AQAAAAAAAAA=")], "AQAAAAAAAAA=");
}

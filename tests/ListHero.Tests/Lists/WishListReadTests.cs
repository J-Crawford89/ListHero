using System.Text.Json;
using ListHero.Application.Authorization;
using ListHero.Application.Lists;
using ListHero.Domain.Entities;

namespace ListHero.Tests.Lists;

public sealed class WishListReadTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly IWishListReadService _reads = new WishListReadService(new ListAccessService());

    [Fact]
    public void Owner_response_contains_no_purchase_fields_or_fulfillment_data()
    {
        var list = new WishList(Guid.NewGuid(), "Wishes", "", Now);
        var item = new WishListItem(list.Id, "Shoes", "", Now);
        var response = _reads.ForOwner(list, [item], new(list.OwnerId), Now);
        var serialized = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("Purchased", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fulfilled", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Guest", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<UnauthorizedAccessException>(() => _reads.ForViewer(list, [item], [], new(list.OwnerId), null, Now));
    }

    [Fact]
    public void Viewer_sees_overpurchase_without_buyer_identity_and_undo_reduces_total()
    {
        var list = new WishList(Guid.NewGuid(), "Wishes", "", Now);
        list.Update(list.Name, list.Description, true);
        var item = new WishListItem(list.Id, "Mugs", "", Now, desiredQuantity: 4);
        var first = new ItemPurchase(item.Id, 3, Guid.NewGuid(), null, Guid.NewGuid(), Now);
        var second = new ItemPurchase(item.Id, 3, null, Guid.NewGuid(), Guid.NewGuid(), Now);
        var response = _reads.ForViewer(list, [item], [first, second], new(), null, Now);
        var result = Assert.Single(response.Items);
        Assert.Equal(6, result.PurchasedQuantity);
        Assert.True(result.IsFulfilled);
        Assert.Equal(2, result.OverpurchasedBy);
        Assert.DoesNotContain(first.UserId!.Value.ToString(), JsonSerializer.Serialize(response));
        Assert.DoesNotContain(second.GuestIdentityId!.Value.ToString(), JsonSerializer.Serialize(response));
        second.Undo(Now);
        result = Assert.Single(_reads.ForViewer(list, [item], [first, second], new(), null, Now).Items);
        Assert.Equal(3, result.PurchasedQuantity);
        Assert.False(result.IsFulfilled);
    }

    [Fact]
    public void Totals_do_not_overflow_when_multiple_marks_exceed_int_maximum()
    {
        var list = new WishList(Guid.NewGuid(), "Wishes", "", Now);
        list.Update(list.Name, list.Description, true);
        var item = new WishListItem(list.Id, "Items", "", Now);
        var purchases = Enumerable.Range(0, 2)
            .Select(_ => new ItemPurchase(item.Id, int.MaxValue, Guid.NewGuid(), null, Guid.NewGuid(), Now));
        var response = _reads.ForViewer(list, [item], purchases, new(), null, Now);
        Assert.Equal(2L * int.MaxValue, Assert.Single(response.Items).PurchasedQuantity);
    }

    [Fact]
    public void Archived_and_unrelated_items_are_excluded_from_list_responses()
    {
        var list = new WishList(Guid.NewGuid(), "Wishes", "", Now);
        var live = new WishListItem(list.Id, "Live", "", Now);
        var archived = new WishListItem(list.Id, "Removed", "", Now);
        archived.Archive(Now);
        var unrelated = new WishListItem(Guid.NewGuid(), "Other list", "", Now);
        Assert.Equal(live.Id, Assert.Single(_reads.ForOwner(list, [live, archived, unrelated], new(list.OwnerId), Now).Items).Id);
    }
}

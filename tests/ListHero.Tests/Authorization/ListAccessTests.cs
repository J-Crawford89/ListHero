using ListHero.Application.Authorization;
using ListHero.Domain.Entities;

namespace ListHero.Tests.Authorization;

public sealed class ListAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly IListAccessService _access = new ListAccessService();

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, true, true)]
    [InlineData(false, true, true, true, true)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, false, true, true, true)]
    public void Access_matches_visibility_identity_and_share_rules(bool isPublic, bool signedIn,
        bool shared, bool canView, bool canMark)
    {
        var list = List(isPublic);
        var viewer = new ViewerIdentity(signedIn ? Guid.NewGuid() : null);
        var permissions = _access.Evaluate(list, viewer, shared ? Share(list) : null, Now);
        Assert.Equal(canView, permissions.CanView);
        Assert.Equal(canView, permissions.CanViewPurchases);
        Assert.Equal(canMark, permissions.CanMarkPurchases);
        Assert.False(permissions.CanEdit);
    }

    [Fact]
    public void Owner_keeps_owner_permissions_even_with_a_valid_share_link()
    {
        var list = List(true);
        var permissions = _access.Evaluate(list, new(list.OwnerId), Share(list), Now);
        Assert.True(permissions.CanEdit);
        Assert.True(permissions.IsOwner);
        Assert.False(permissions.CanViewPurchases);
        Assert.False(permissions.CanMarkPurchases);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("archived")]
    [InlineData("different-list")]
    public void Invalid_share_access_cannot_open_a_private_list(string reason)
    {
        var list = List();
        var share = Share(reason == "different-list" ? List() : list,
            reason == "expired" ? Now : null);
        if (reason == "revoked") share.Revoke(Now);
        if (reason == "archived") share.Archive(Now);
        Assert.False(_access.Evaluate(list, new(), share, Now).CanView);
    }

    [Fact]
    public void Expired_share_does_not_allow_an_anonymous_public_viewer_to_mark()
    {
        var list = List(true);
        var result = _access.Evaluate(list, new(), Share(list, Now), Now);
        Assert.True(result.CanView);
        Assert.False(result.CanMarkPurchases);
    }

    [Fact]
    public void Archived_list_is_unavailable_even_to_owner_and_share_holders()
    {
        var list = List(true);
        list.Archive(Now);
        Assert.False(_access.Evaluate(list, new(list.OwnerId), Share(list), Now).CanView);
    }

    [Fact]
    public void Shared_link_does_not_confer_ownership_of_other_guests_marks()
    {
        var list = List();
        var firstGuest = new ViewerIdentity(GuestIdentityId: Guid.NewGuid());
        var secondGuest = new ViewerIdentity(GuestIdentityId: Guid.NewGuid());
        var item = new WishListItem(list.Id, "Gift", "", Now);
        var purchase = new ItemPurchase(item.Id, 1, null, firstGuest.GuestIdentityId, Guid.NewGuid(), Now);
        var share = Share(list);
        Assert.True(_access.CanUndo(purchase, item, list, firstGuest, share, Now));
        Assert.False(_access.CanUndo(purchase, item, list, secondGuest, share, Now));
        share.Revoke(Now);
        Assert.False(_access.CanUndo(purchase, item, list, firstGuest, share, Now));
    }

    [Fact]
    public void Signed_in_buyer_can_undo_only_their_own_active_marks()
    {
        var list = List(true);
        var buyer = new ViewerIdentity(Guid.NewGuid());
        var item = new WishListItem(list.Id, "Gift", "", Now);
        var purchase = new ItemPurchase(item.Id, 1, buyer.UserId, null, Guid.NewGuid(), Now);
        Assert.True(_access.CanUndo(purchase, item, list, buyer, null, Now));
        Assert.False(_access.CanUndo(purchase, item, list, new(Guid.NewGuid()), null, Now));
        purchase.Undo(Now);
        Assert.False(_access.CanUndo(purchase, item, list, buyer, null, Now));
    }

    [Fact]
    public void Undo_checks_the_target_item_and_list_and_excludes_archived_items()
    {
        var list = List(true);
        var otherList = List(true);
        var buyer = new ViewerIdentity(Guid.NewGuid());
        var item = new WishListItem(list.Id, "Gift", "", Now);
        var otherItem = new WishListItem(otherList.Id, "Other gift", "", Now);
        var purchase = new ItemPurchase(item.Id, 1, buyer.UserId, null, Guid.NewGuid(), Now);
        Assert.False(_access.CanUndo(purchase, item, otherList, buyer, null, Now));
        Assert.False(_access.CanUndo(purchase, otherItem, otherList, buyer, null, Now));
        item.Archive(Now);
        Assert.False(_access.CanUndo(purchase, item, list, buyer, null, Now));
    }

    private static WishList List(bool isPublic = false)
    {
        var list = new WishList(Guid.NewGuid(), "Birthday", "A few wishes", Now);
        list.Update(list.Name, list.Description, isPublic);
        return list;
    }

    private static ListShareLink Share(WishList list, DateTimeOffset? expiresAt = null)
        => new(list.Id, new string('A', 64), "protected-token", Now.AddDays(-1), expiresAt);
}

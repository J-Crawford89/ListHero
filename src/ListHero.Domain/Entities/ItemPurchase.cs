using ListHero.Domain.Common;

namespace ListHero.Domain.Entities;

public sealed class ItemPurchase : Entity
{
    public Guid WishListItemId { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid? GuestIdentityId { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public int Quantity { get; private set; }
    public DateTimeOffset? UndoneAt { get; private set; }
    public bool IsActive => !IsArchived && UndoneAt is null;
    public byte[] RowVersion { get; private set; } = [];

    private ItemPurchase() { }

    public ItemPurchase(Guid wishListItemId, int quantity, Guid? userId, Guid? guestIdentityId,
        Guid idempotencyKey, DateTimeOffset createdAt) : base(createdAt)
    {
        WishListItemId = Guard.Id(wishListItemId, nameof(wishListItemId));
        if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (userId.HasValue == guestIdentityId.HasValue)
            throw new ArgumentException("A purchase mark must belong to exactly one user or guest.");
        if (userId.HasValue) Guard.Id(userId.Value, nameof(userId));
        if (guestIdentityId.HasValue) Guard.Id(guestIdentityId.Value, nameof(guestIdentityId));
        UserId = userId;
        GuestIdentityId = guestIdentityId;
        IdempotencyKey = Guard.Id(idempotencyKey, nameof(idempotencyKey));
        Quantity = quantity;
    }

    public void Undo(DateTimeOffset undoneAt) => UndoneAt ??= undoneAt;
}

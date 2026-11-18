using ListHero.Domain.Common;
namespace ListHero.Domain.Entities;

public sealed class SavedList : Entity
{
    public Guid UserId { get; private set; }
    public Guid WishListId { get; private set; }
    public Guid? ShareLinkId { get; private set; }
    public bool IsPinned { get; private set; }
    private SavedList() { }
    public SavedList(Guid userId, Guid listId, Guid? shareLinkId, bool pinned, DateTimeOffset now) : base(now)
    { UserId = Guard.Id(userId, nameof(userId)); WishListId = Guard.Id(listId, nameof(listId)); Update(shareLinkId, pinned); }
    public void Update(Guid? shareLinkId, bool pinned) { ShareLinkId = shareLinkId; IsPinned = pinned; }
}

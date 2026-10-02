using ListHero.Domain.Common;

namespace ListHero.Domain.Entities;

public sealed class WishList : Entity
{
    public Guid OwnerId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public bool IsPublic { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private WishList() { }

    public WishList(Guid ownerId, string name, string description, DateTimeOffset createdAt) : base(createdAt)
    {
        OwnerId = Guard.Id(ownerId, nameof(ownerId));
        Update(name, description, isPublic: false);
    }

    public void Update(string name, string description, bool isPublic)
    {
        var validName = Guard.Required(name, 200, nameof(name));
        var validDescription = Guard.Description(description);
        Name = validName;
        Description = validDescription;
        IsPublic = isPublic;
    }
}

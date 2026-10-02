using ListHero.Domain.Common;

namespace ListHero.Domain.Entities;

public sealed class WishListItem : Entity
{
    public Guid WishListId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public double? ApproximateUnitPrice { get; private set; }
    public int DesiredQuantity { get; private set; } = 1;
    public int Priority { get; private set; }
    public int DisplayOrder { get; private set; }
    public string? Url { get; private set; }
    public string? ImageUrl { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private WishListItem() { }

    public WishListItem(Guid wishListId, string name, string description, DateTimeOffset createdAt,
        double? approximateUnitPrice = null, int desiredQuantity = 1, int priority = 0,
        int displayOrder = 0, string? url = null, string? imageUrl = null) : base(createdAt)
    {
        WishListId = Guard.Id(wishListId, nameof(wishListId));
        Update(name, description, approximateUnitPrice, desiredQuantity, priority, displayOrder, url, imageUrl);
    }

    public void Update(string name, string description, double? approximateUnitPrice, int desiredQuantity,
        int priority, int displayOrder, string? url, string? imageUrl)
    {
        if (desiredQuantity < 1) throw new ArgumentOutOfRangeException(nameof(desiredQuantity));
        if (approximateUnitPrice is double price && (!double.IsFinite(price) || price < 0))
            throw new ArgumentOutOfRangeException(nameof(approximateUnitPrice));
        var validName = Guard.Required(name, 200, nameof(name));
        var validDescription = Guard.Description(description);
        var validUrl = Guard.WebUrl(url, nameof(url));
        var validImageUrl = Guard.WebUrl(imageUrl, nameof(imageUrl));
        Name = validName;
        Description = validDescription;
        ApproximateUnitPrice = approximateUnitPrice;
        DesiredQuantity = desiredQuantity;
        Priority = priority;
        DisplayOrder = displayOrder;
        Url = validUrl;
        ImageUrl = validImageUrl;
    }
}

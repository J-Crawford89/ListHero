using System.Text.Json.Serialization;

namespace ListHero.Contracts.Lists;

public sealed record WishListItemResponse(Guid Id, string Name, string Description,
    double? ApproximateUnitPrice, int DesiredQuantity, int Priority, int DisplayOrder,
    string? Url, string? ImageUrl, string RowVersion = "");

// Deliberately separate response shapes: owner reads never serialize purchase data.
public sealed record OwnerWishListResponse(Guid Id, string Name, string Description,
    bool IsPublic, IReadOnlyList<WishListItemResponse> Items, string RowVersion = "");

public sealed record ViewerWishListItemResponse(WishListItemResponse Item, long PurchasedQuantity,
    bool IsFulfilled, long OverpurchasedBy);

public sealed record ViewerWishListResponse(Guid Id, string Name, string Description,
    bool IsPublic, bool CanMarkPurchases, IReadOnlyList<ViewerWishListItemResponse> Items,
    IReadOnlyList<OwnPurchaseMarkResponse>? MyMarks = null);

public sealed record ItemEditWarningResponse(bool MayHavePurchaseMarks);

public sealed record ShareLinkResponse(Guid Id, string? Token, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, bool IsActive)
{
    public override string ToString() => "ShareLinkResponse { Token = [redacted] }";
}

public sealed record GuestCredentialResponse(string Credential)
{
    public override string ToString() => "GuestCredentialResponse { Credential = [redacted] }";
}

public sealed record OwnPurchaseMarkResponse(Guid Id, Guid ItemId, int Quantity, DateTimeOffset CreatedAt,
    DateTimeOffset? UndoneAt, string RowVersion);

public sealed record ListViewResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OwnerWishListResponse? Owner,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ViewerWishListResponse? Viewer);

public static class CapabilityHeaders
{
    public const string Share = "X-ListHero-Share-Key";
    public const string Guest = "X-ListHero-Guest-Key";
}

using System.ComponentModel.DataAnnotations;

namespace ListHero.Contracts.Lists;

public class CreateWishListRequest
{
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string Description { get; set; } = string.Empty;
}

public class CreateWishListItemRequest
{
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string Description { get; set; } = string.Empty;

    [Range(0, double.MaxValue)]
    public double? ApproximateUnitPrice { get; set; }

    [Range(1, int.MaxValue)]
    public int DesiredQuantity { get; set; } = 1;

    public int Priority { get; set; }
    public int DisplayOrder { get; set; }

    [MaxLength(2048), HttpUrl]
    public string? Url { get; set; }

    [MaxLength(2048), HttpUrl]
    public string? ImageUrl { get; set; }
}

public sealed class HttpUrlAttribute : ValidationAttribute
{
    public HttpUrlAttribute() => ErrorMessage = "Enter a full HTTP or HTTPS URL.";

    public override bool IsValid(object? value) => value is null
        || value is string text && (string.IsNullOrWhiteSpace(text)
            || Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https");
}

public sealed record WishListSummaryResponse(Guid Id, string Name, string Description,
    bool IsPublic, DateTimeOffset CreatedAt);

public sealed class UpdateWishListRequest : CreateWishListRequest
{
    public bool IsPublic { get; set; }
    [Required, MaxLength(32)] public string RowVersion { get; set; } = string.Empty;
}

public sealed class UpdateWishListItemRequest : CreateWishListItemRequest
{
    [Required, MaxLength(32)] public string RowVersion { get; set; } = string.Empty;
}

public sealed class ArchiveRequest
{
    [Required, MaxLength(32)] public string RowVersion { get; set; } = string.Empty;
}

public sealed class CreateShareLinkRequest
{
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class CreatePurchaseMarkRequest
{
    [Range(1, int.MaxValue)] public int Quantity { get; set; } = 1;
    public Guid IdempotencyKey { get; set; }
}

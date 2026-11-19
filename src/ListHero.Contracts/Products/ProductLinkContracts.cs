using System.ComponentModel.DataAnnotations;

namespace ListHero.Contracts.Products;

public sealed record ProductLinkRequest([property: Required, StringLength(2048)] string Url);
public sealed record ProductLinkResult(string Status, string? Name = null, string? Description = null,
    string? ImageUrl = null, double? Price = null, string? Transport = null)
{
    public bool HasDetails => Name is not null || Description is not null || ImageUrl is not null || Price is not null;
}

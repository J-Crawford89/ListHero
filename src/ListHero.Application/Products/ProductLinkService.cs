using ListHero.Application.Identity;
using ListHero.Contracts.Products;

namespace ListHero.Application.Products;

public interface IProductLinkImporter
{
    Task<ProductLinkResult> ImportAsync(Guid userId, string url, CancellationToken ct);
}
public interface IProductLinkService
{
    Task<ProductLinkResult> ImportAsync(ExternalUserIdentity identity, ProductLinkRequest request, CancellationToken ct);
}

public sealed class ProductLinkService(IUserService users, IProductLinkImporter importer) : IProductLinkService
{
    public async Task<ProductLinkResult> ImportAsync(ExternalUserIdentity identity, ProductLinkRequest request, CancellationToken ct)
    {
        var user = await users.ResolveAsync(identity, ct);
        return await importer.ImportAsync(user.Id, request.Url, ct);
    }
}

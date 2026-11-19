using ListHero.Contracts.Products;

namespace ListHero.Client.Abstractions.Api;

public interface IProductLinkApi
{
    Task<ProductLinkResult> ImportAsync(string url, CancellationToken ct = default);
}

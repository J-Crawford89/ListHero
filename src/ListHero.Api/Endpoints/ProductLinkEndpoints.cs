using ListHero.Api.Authentication;
using ListHero.Application.Products;
using ListHero.Contracts.Identity;
using ListHero.Contracts.Products;

namespace ListHero.Api.Endpoints;

public static class ProductLinkEndpoints
{
    public static void MapProductLinkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/item-metadata", async (ProductLinkRequest request, HttpContext context, IProductLinkService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(await service.ImportAsync(context.User.ToExternalIdentity(), request, ct));
        }).RequireAuthorization(AppRoles.User).AddEndpointFilter<RequestValidationFilter<ProductLinkRequest>>().WithName("ImportProductLink");
    }
}

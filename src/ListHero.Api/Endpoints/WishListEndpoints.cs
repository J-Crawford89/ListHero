using ListHero.Api.Authentication;
using ListHero.Application.Lists;
using ListHero.Contracts.Identity;
using ListHero.Contracts.Lists;

namespace ListHero.Api.Endpoints;

public static class WishListEndpoints
{
    public static void MapWishListEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/lists").RequireAuthorization(AppRoles.User).WithTags("Wish lists");
        group.MapGet("/mine", async (HttpContext context, IWishListService service, CancellationToken cancellationToken)
            => TypedResults.Ok(await service.GetMineAsync(context.User.ToExternalIdentity(), cancellationToken)))
            .WithName("GetMyWishLists");

        group.MapPost("/", async (CreateWishListRequest request, HttpContext context,
            IWishListService service, CancellationToken cancellationToken) =>
        {
            var list = await service.CreateAsync(context.User.ToExternalIdentity(), request, cancellationToken);
            return TypedResults.Created($"/api/lists/{list.Id}/owner", list);
        }).AddEndpointFilter<RequestValidationFilter<CreateWishListRequest>>().WithName("CreateWishList");

        group.MapGet("/{listId:guid}/owner", async (Guid listId, HttpContext context,
            IWishListService service, CancellationToken cancellationToken)
            => TypedResults.Ok(await service.GetOwnerViewAsync(context.User.ToExternalIdentity(), listId, cancellationToken)))
            .WithName("GetOwnedWishList");

        // Returns the updated owner view after adding an item.
        group.MapPost("/{listId:guid}/items", async (Guid listId, CreateWishListItemRequest request,
            HttpContext context, IWishListService service, CancellationToken cancellationToken)
            => TypedResults.Ok(await service.AddItemAsync(context.User.ToExternalIdentity(), listId, request, cancellationToken)))
            .AddEndpointFilter<RequestValidationFilter<CreateWishListItemRequest>>().WithName("AddWishListItem");

        group.MapPut("/{listId:guid}", async (Guid listId, UpdateWishListRequest request, HttpContext context, IWishListService service, CancellationToken ct)
            => TypedResults.Ok(await service.UpdateAsync(context.User.ToExternalIdentity(), listId, request, ct)))
            .AddEndpointFilter<RequestValidationFilter<UpdateWishListRequest>>();
        group.MapPost("/{listId:guid}/archive", async (Guid listId, ArchiveRequest request, HttpContext context, IWishListService service, CancellationToken ct) =>
        {
            await service.ArchiveAsync(context.User.ToExternalIdentity(), listId, request, ct);
            return TypedResults.NoContent();
        }).AddEndpointFilter<RequestValidationFilter<ArchiveRequest>>();
        group.MapPut("/{listId:guid}/items/{itemId:guid}", async (Guid listId, Guid itemId, UpdateWishListItemRequest request, HttpContext context, IWishListService service, CancellationToken ct)
            => TypedResults.Ok(await service.UpdateItemAsync(context.User.ToExternalIdentity(), listId, itemId, request, ct)))
            .AddEndpointFilter<RequestValidationFilter<UpdateWishListItemRequest>>();
        group.MapPost("/{listId:guid}/items/{itemId:guid}/archive", async (Guid listId, Guid itemId, ArchiveRequest request, HttpContext context, IWishListService service, CancellationToken ct)
            => TypedResults.Ok(await service.ArchiveItemAsync(context.User.ToExternalIdentity(), listId, itemId, request, ct)))
            .AddEndpointFilter<RequestValidationFilter<ArchiveRequest>>();
        group.MapGet("/{listId:guid}/items/{itemId:guid}/edit-warning", async (Guid listId, Guid itemId, HttpContext context, IWishListService service, CancellationToken ct)
            => TypedResults.Ok(await service.EditWarningAsync(context.User.ToExternalIdentity(), listId, itemId, ct)));
    }
}

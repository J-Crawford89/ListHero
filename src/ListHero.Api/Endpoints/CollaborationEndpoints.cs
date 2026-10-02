using ListHero.Api.Authentication;
using ListHero.Application.Collaboration;
using ListHero.Contracts.Identity;
using ListHero.Contracts.Lists;

namespace ListHero.Api.Endpoints;

public static class CollaborationEndpoints
{
    public static void MapCollaborationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var owner = endpoints.MapGroup("/api/lists/{listId:guid}/share-links").RequireAuthorization(AppRoles.User);
        owner.MapGet("/", async (Guid listId, HttpContext context, IShareLinkService service, CancellationToken ct)
            => TypedResults.Ok(await service.GetAsync(context.User.ToExternalIdentity(), listId, ct)));
        owner.MapPost("/", async (Guid listId, CreateShareLinkRequest request, HttpContext context, IShareLinkService service, CancellationToken ct)
            => TypedResults.Ok(await service.CreateAsync(context.User.ToExternalIdentity(), listId, request, ct)));
        owner.MapPost("/{linkId:guid}/revoke", async (Guid listId, Guid linkId, HttpContext context, IShareLinkService service, CancellationToken ct) =>
        {
            await service.RevokeAsync(context.User.ToExternalIdentity(), listId, linkId, ct);
            return TypedResults.NoContent();
        });

        var viewer = endpoints.MapGroup("/api/lists/{listId:guid}").AllowAnonymous();
        // AllowAnonymous does not turn a rejected bearer token into a guest request.
        viewer.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.Request.Headers.ContainsKey("Authorization") && http.User.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();
            var scope = http.RequestServices.GetRequiredService<IConfiguration>()["Authentication:RequiredScope"] ?? "access_as_user";
            if (http.User.Identity?.IsAuthenticated == true && !http.User.FindAll("scp")
                .Any(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope)))
                return Results.Forbid();
            return await next(context);
        });
        viewer.MapGet("/view", async (Guid listId, HttpContext context, IListViewerService service, CancellationToken ct)
            => TypedResults.Ok(await service.GetAsync(listId, ReadAccess(context), ct)));
        viewer.MapPost("/guest-credential", async (Guid listId, HttpContext context, IPurchaseService service, CancellationToken ct)
            => TypedResults.Ok(await service.IssueGuestAsync(listId, ReadAccess(context), ct)));
        viewer.MapPost("/items/{itemId:guid}/marks", async (Guid listId, Guid itemId, CreatePurchaseMarkRequest request,
            HttpContext context, IPurchaseService service, CancellationToken ct)
            => TypedResults.Ok(await service.MarkAsync(listId, itemId, ReadAccess(context), request, ct)))
            .AddEndpointFilter<RequestValidationFilter<CreatePurchaseMarkRequest>>();
        viewer.MapPost("/items/{itemId:guid}/marks/{markId:guid}/undo", async (Guid listId, Guid itemId, Guid markId,
            ArchiveRequest request, HttpContext context, IPurchaseService service, CancellationToken ct) =>
        {
            await service.UndoAsync(listId, itemId, markId, ReadAccess(context), request, ct);
            return TypedResults.NoContent();
        }).AddEndpointFilter<RequestValidationFilter<ArchiveRequest>>();
    }

    private static ListRequestAccess ReadAccess(HttpContext context) => new(
        context.User.Identity?.IsAuthenticated == true ? context.User.ToExternalIdentity() : null,
        context.Request.Headers[CapabilityHeaders.Share].FirstOrDefault(),
        context.Request.Headers[CapabilityHeaders.Guest].FirstOrDefault());
}

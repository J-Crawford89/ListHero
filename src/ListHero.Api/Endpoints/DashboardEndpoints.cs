using ListHero.Api.Authentication;
using ListHero.Application.Collaboration;
using ListHero.Application.Lists;
using ListHero.Contracts.Identity;
using ListHero.Contracts.Lists;
namespace ListHero.Api.Endpoints;
public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/dashboard").RequireAuthorization(AppRoles.User);
        group.MapGet("/", async (HttpContext http, IDashboardService service, CancellationToken ct)
            => TypedResults.Ok(await service.GetAsync(http.User.ToExternalIdentity(), ct)));
        group.MapGet("/saved", async (int? page, HttpContext http, IDashboardService service, CancellationToken ct)
            => TypedResults.Ok(await service.SavedAsync(http.User.ToExternalIdentity(), page ?? 1, ct)));
        group.MapPut("/saved/{listId:guid}", async (Guid listId, SaveListRequest request, HttpContext http, IDashboardService service, CancellationToken ct) =>
        {
            await service.SaveAsync(listId, new ListRequestAccess(http.User.ToExternalIdentity(), http.Request.Headers[CapabilityHeaders.Share].FirstOrDefault(), null), request.IsPinned, ct);
            return TypedResults.NoContent();
        });
        group.MapDelete("/saved/{listId:guid}", async (Guid listId, HttpContext http, IDashboardService service, CancellationToken ct) =>
        { await service.RemoveAsync(http.User.ToExternalIdentity(), listId, ct); return TypedResults.NoContent(); });
    }
}

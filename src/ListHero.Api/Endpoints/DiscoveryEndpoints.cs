using ListHero.Api.Authentication;
using ListHero.Application.Discovery;
using ListHero.Contracts.Discovery;
using ListHero.Contracts.Identity;

namespace ListHero.Api.Endpoints;

public static class DiscoveryEndpoints
{
    public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var discovery = endpoints.MapGroup("/api/discovery").AllowAnonymous().WithTags("Discovery");
        discovery.MapGet("/lists", async (string? q, int? page, int? pageSize, IDiscoveryService service, CancellationToken ct)
            => TypedResults.Ok(await service.GetListsAsync(new(q, page ?? 1, pageSize ?? 20), ct)));
        discovery.MapGet("/people", async (string? q, int? page, int? pageSize, IDiscoveryService service, CancellationToken ct)
            => TypedResults.Ok(await service.GetPeopleAsync(new(q, page ?? 1, pageSize ?? 20), ct)));
        discovery.MapGet("/people/{handle}", async (string handle, int? page, int? pageSize, IDiscoveryService service, CancellationToken ct)
            => TypedResults.Ok(await service.GetProfileAsync(handle, new(null, page ?? 1, pageSize ?? 20), ct)));

        var profile = endpoints.MapGroup("/api/profile").RequireAuthorization(AppRoles.User).WithTags("Public profile");
        profile.MapGet("/me", async (HttpContext context, IPublicProfileService service, CancellationToken ct)
            => TypedResults.Ok(await service.GetAsync(context.User.ToExternalIdentity(), ct)));
        profile.MapPut("/me", async (UpdatePublicProfileRequest request, HttpContext context, IPublicProfileService service, CancellationToken ct)
            => TypedResults.Ok(await service.UpdateAsync(context.User.ToExternalIdentity(), request, ct)))
            .AddEndpointFilter<RequestValidationFilter<UpdatePublicProfileRequest>>();
    }
}

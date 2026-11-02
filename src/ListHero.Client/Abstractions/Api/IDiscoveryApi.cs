using ListHero.Contracts.Discovery;

namespace ListHero.Client.Abstractions.Api;

public interface IDiscoveryApi
{
    Task<DiscoveryPage<PublicListSummary>> GetListsAsync(DiscoveryQuery query, CancellationToken ct = default);
    Task<DiscoveryPage<PublicPersonSummary>> GetPeopleAsync(DiscoveryQuery query, CancellationToken ct = default);
    Task<PublicProfileResponse> GetProfileAsync(string handle, int page = 1, CancellationToken ct = default);
}
public interface IPublicProfileApi
{
    Task<OwnProfileResponse> GetAsync(CancellationToken ct = default);
    Task<OwnProfileResponse> UpdateAsync(UpdatePublicProfileRequest request, CancellationToken ct = default);
}

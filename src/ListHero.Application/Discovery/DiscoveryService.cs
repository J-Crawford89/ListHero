using ListHero.Application.Common;
using ListHero.Contracts.Discovery;
using ListHero.Domain.Entities;

namespace ListHero.Application.Discovery;

public interface IDiscoveryStore
{
    Task<DiscoveryPage<PublicListSummary>> GetListsAsync(DiscoveryQuery query, CancellationToken ct);
    Task<DiscoveryPage<PublicPersonSummary>> GetPeopleAsync(DiscoveryQuery query, CancellationToken ct);
    Task<PublicProfileResponse?> GetProfileAsync(string handle, DiscoveryQuery query, CancellationToken ct);
}
public interface IDiscoveryService
{
    Task<DiscoveryPage<PublicListSummary>> GetListsAsync(DiscoveryQuery query, CancellationToken ct = default);
    Task<DiscoveryPage<PublicPersonSummary>> GetPeopleAsync(DiscoveryQuery query, CancellationToken ct = default);
    Task<PublicProfileResponse> GetProfileAsync(string handle, DiscoveryQuery query, CancellationToken ct = default);
}
public sealed class DiscoveryService(IDiscoveryStore store) : IDiscoveryService
{
    public Task<DiscoveryPage<PublicListSummary>> GetListsAsync(DiscoveryQuery query, CancellationToken ct = default)
        => store.GetListsAsync(Validate(query), ct);

    public Task<DiscoveryPage<PublicPersonSummary>> GetPeopleAsync(DiscoveryQuery query, CancellationToken ct = default)
    {
        var text = query.Query?.Trim();
        if (text?.StartsWith('@') == true) text = text[1..];
        var valid = Validate(query with { Query = text });
        return string.IsNullOrEmpty(valid.Query)
            ? Task.FromResult(new DiscoveryPage<PublicPersonSummary>([], valid.Page, valid.PageSize, false))
            : store.GetPeopleAsync(valid, ct);
    }

    public async Task<PublicProfileResponse> GetProfileAsync(string handle, DiscoveryQuery query, CancellationToken ct = default)
        => await store.GetProfileAsync(User.NormalizeHandle(handle), Validate(query with { Query = null }), ct)
            ?? throw new ResourceNotFoundException();

    private static DiscoveryQuery Validate(DiscoveryQuery query)
    {
        var text = query.Query?.Trim() ?? "";
        if (text.Length is 1 or > 100) throw new ArgumentException("Search using 2–100 characters, or leave the search empty.");
        if (query.Page is < 1 or > 100 || query.PageSize is < 1 or > 50)
            throw new ArgumentException("Choose a page from 1–100 and a page size from 1–50.");
        return query with { Query = text };
    }
}

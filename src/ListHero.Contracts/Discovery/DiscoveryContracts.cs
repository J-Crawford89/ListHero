using System.ComponentModel.DataAnnotations;

namespace ListHero.Contracts.Discovery;

public sealed record DiscoveryQuery(string? Query = null, int Page = 1, int PageSize = 20);
public sealed record DiscoveryPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, bool HasMore);
public sealed record PublicAuthorSummary(string DisplayName, string Handle);
public sealed record PublicListSummary(Guid Id, string Name, string Description, DateTimeOffset CreatedAt, PublicAuthorSummary Author);
public sealed record PublicPersonSummary(string DisplayName, string Handle);
public sealed record PublicProfileResponse(PublicAuthorSummary Author, DiscoveryPage<PublicListSummary> Lists);
public sealed record OwnProfileResponse(string? DisplayName, string? Handle, string SuggestedDisplayName, string RowVersion);

public sealed class UpdatePublicProfileRequest
{
    [Required, MaxLength(100)] public string DisplayName { get; set; } = "";
    [Required, MaxLength(31)] public string Handle { get; set; } = "";
    [Required, MaxLength(32)] public string RowVersion { get; set; } = "";
    [Range(typeof(bool), "true", "true", ErrorMessage = "Confirm that your public name and public lists can appear in discovery.")]
    public bool ConfirmDiscovery { get; set; }
}

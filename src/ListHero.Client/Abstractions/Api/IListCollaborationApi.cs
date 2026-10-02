using ListHero.Contracts.Lists;

namespace ListHero.Client.Abstractions.Api;

public sealed record ViewerCredentials(bool IsSignedIn, string? ShareToken = null, string? GuestCredential = null)
{
    public override string ToString() => "ViewerCredentials { Credentials = [redacted] }";
}
public interface IListCollaborationApi
{
    Task<IReadOnlyList<ShareLinkResponse>> GetLinksAsync(Guid listId, CancellationToken ct = default);
    Task<ShareLinkResponse> CreateLinkAsync(Guid listId, CreateShareLinkRequest request, CancellationToken ct = default);
    Task RevokeLinkAsync(Guid listId, Guid linkId, CancellationToken ct = default);
    Task<ListViewResponse> ViewAsync(Guid listId, ViewerCredentials credentials, CancellationToken ct = default);
    Task<GuestCredentialResponse> IssueGuestAsync(Guid listId, ViewerCredentials credentials, CancellationToken ct = default);
    Task<OwnPurchaseMarkResponse> MarkAsync(Guid listId, Guid itemId, CreatePurchaseMarkRequest request, ViewerCredentials credentials, CancellationToken ct = default);
    Task UndoAsync(Guid listId, Guid itemId, Guid markId, ArchiveRequest request, ViewerCredentials credentials, CancellationToken ct = default);
}

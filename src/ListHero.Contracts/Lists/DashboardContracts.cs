namespace ListHero.Contracts.Lists;
public sealed record SavedListSummary(Guid ListId, string Name, bool IsAvailable, bool IsOwner, bool IsPinned);
public sealed record SavedListPage(IReadOnlyList<SavedListSummary> Items, int Page, bool HasMore);
public sealed record DashboardResponse(IReadOnlyList<SavedListSummary> Pinned, IReadOnlyList<WishListSummaryResponse> Owned, IReadOnlyList<SavedListSummary> Saved);
public sealed class SaveListRequest
{
    // Omitted/null preserves an existing pin; a new shortcut starts unpinned.
    public bool? IsPinned { get; set; }
}

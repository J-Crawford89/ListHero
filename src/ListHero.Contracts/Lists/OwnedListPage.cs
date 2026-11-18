namespace ListHero.Contracts.Lists;

public static class OwnedListPaging
{
    public const int DefaultPageSize = 15;
    public const int MaximumPageSize = 100;
    public const int MaximumPage = 1000;
}

public sealed record OwnedListPage(IReadOnlyList<WishListSummaryResponse> Items, int Page, int PageSize, bool HasMore);

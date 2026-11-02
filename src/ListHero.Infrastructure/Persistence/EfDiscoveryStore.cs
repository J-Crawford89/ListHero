using ListHero.Application.Discovery;
using ListHero.Contracts.Discovery;
using ListHero.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ListHero.Infrastructure.Persistence;

public sealed class EfDiscoveryStore(ListHeroDbContext db) : IDiscoveryStore
{
    private const string SearchCollation = "Latin1_General_100_CI_AI_SC";
    private IQueryable<User> PublicPeople => db.Users.AsNoTracking().Where(u => u.ArchivedAt == null
        && u.PublicDisplayName != null && u.PublicHandle != null
        && db.WishLists.Any(l => l.OwnerId == u.Id && l.IsPublic && l.ArchivedAt == null));

    private sealed class ListRow
    {
        public WishList List { get; set; } = null!;
        public User Author { get; set; } = null!;
    }
    private IQueryable<ListRow> PublicLists =>
        from list in db.WishLists.AsNoTracking()
        join user in PublicPeople on list.OwnerId equals user.Id
        where list.IsPublic && list.ArchivedAt == null
        select new ListRow { List = list, Author = user };

    private static IQueryable<PublicListSummary> Summaries(IQueryable<ListRow> rows) => rows.Select(row =>
        new PublicListSummary(row.List.Id, row.List.Name,
            row.List.Description.Length > 200 ? row.List.Description.Substring(0, 200) : row.List.Description,
            row.List.CreatedAt, new PublicAuthorSummary(row.Author.PublicDisplayName!, row.Author.PublicHandle!)));

    public Task<DiscoveryPage<PublicListSummary>> GetListsAsync(DiscoveryQuery query, CancellationToken ct)
    {
        var text = query.Query!;
        // Search the complete description before projecting its short public snippet.
        var lists = PublicLists.Where(row => text == "" || EF.Functions.Collate(row.List.Name, SearchCollation).Contains(text)
            || EF.Functions.Collate(row.List.Description, SearchCollation).Contains(text));
        return PageAsync(Summaries(lists.OrderByDescending(row => text != "" && EF.Functions.Collate(row.List.Name, SearchCollation) == text)
            .ThenByDescending(row => row.List.CreatedAt).ThenBy(row => row.List.Id)), query, ct);
    }

    public Task<DiscoveryPage<PublicPersonSummary>> GetPeopleAsync(DiscoveryQuery query, CancellationToken ct)
    {
        var text = query.Query!.TrimStart('@');
        var handleText = text.ToLowerInvariant();
        return PageAsync(PublicPeople.Where(u => EF.Functions.Collate(u.PublicDisplayName!, SearchCollation).Contains(text)
                || u.PublicHandle!.Contains(handleText))
            .OrderByDescending(u => u.PublicHandle == handleText)
            .ThenBy(u => EF.Functions.Collate(u.PublicDisplayName!, SearchCollation)).ThenBy(u => u.PublicHandle)
            .Select(u => new PublicPersonSummary(u.PublicDisplayName!, u.PublicHandle!)), query, ct);
    }

    public async Task<PublicProfileResponse?> GetProfileAsync(string handle, DiscoveryQuery query, CancellationToken ct)
    {
        var author = await PublicPeople.Where(u => u.PublicHandle == handle)
            .Select(u => new PublicAuthorSummary(u.PublicDisplayName!, u.PublicHandle!)).SingleOrDefaultAsync(ct);
        if (author is null) return null;
        var lists = await PageAsync(Summaries(PublicLists.Where(row => row.Author.PublicHandle == handle)
            .OrderByDescending(row => row.List.CreatedAt).ThenBy(row => row.List.Id)), query, ct);
        return new(author, lists);
    }

    private static async Task<DiscoveryPage<T>> PageAsync<T>(IQueryable<T> source, DiscoveryQuery query, CancellationToken ct)
    {
        var rows = await source.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize + 1).ToListAsync(ct);
        return new(rows.Take(query.PageSize).ToArray(), query.Page, query.PageSize, rows.Count > query.PageSize);
    }
}

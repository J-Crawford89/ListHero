using ListHero.Client.Abstractions;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Lists;

namespace ListHero.Client.Services;

public interface IListViewerClient
{
    Task<ListViewResponse> LoadAsync(Guid listId, bool signedIn, string? shareToken, CancellationToken ct = default);
    Task<OwnPurchaseMarkResponse> MarkAsync(Guid listId, Guid itemId, bool signedIn, string? shareToken, CreatePurchaseMarkRequest request, CancellationToken ct = default);
    Task UndoAsync(Guid listId, OwnPurchaseMarkResponse mark, bool signedIn, string? shareToken, CancellationToken ct = default);
}

// Client workflow: persist guest ownership before creating any anonymous purchase mark.
public sealed class ListViewerClient(IListCollaborationApi api, IGuestCredentialStore guests) : IListViewerClient
{
    public async Task<ListViewResponse> LoadAsync(Guid listId, bool signedIn, string? shareToken, CancellationToken ct = default)
        => await api.ViewAsync(listId, new(signedIn, shareToken, await guests.ReadAsync()), ct);
    public async Task<OwnPurchaseMarkResponse> MarkAsync(Guid listId, Guid itemId, bool signedIn, string? shareToken, CreatePurchaseMarkRequest request, CancellationToken ct = default)
    {
        var credential = await guests.ReadAsync();
        if (!signedIn)
        {
            var issued = await api.IssueGuestAsync(listId, new(false, shareToken, credential), ct);
            await guests.WriteAsync(issued.Credential);
            credential = issued.Credential;
        }
        return await api.MarkAsync(listId, itemId, request, new(signedIn, shareToken, credential), ct);
    }
    public async Task UndoAsync(Guid listId, OwnPurchaseMarkResponse mark, bool signedIn, string? shareToken, CancellationToken ct = default)
        => await api.UndoAsync(listId, mark.ItemId, mark.Id, new() { RowVersion = mark.RowVersion },
            new(signedIn, shareToken, await guests.ReadAsync()), ct);
}

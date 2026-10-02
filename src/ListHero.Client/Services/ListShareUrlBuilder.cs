using ListHero.Client.Abstractions;

namespace ListHero.Client.Services;

public sealed class ListShareUrlBuilder : IListShareUrlBuilder
{
    private readonly Uri publicWebBaseUri;
    public ListShareUrlBuilder(Uri publicWebBaseUri)
    {
        if (!publicWebBaseUri.IsAbsoluteUri || publicWebBaseUri.Scheme is not ("https" or "http")
            || !publicWebBaseUri.AbsolutePath.EndsWith('/') || publicWebBaseUri.Query.Length != 0 || publicWebBaseUri.Fragment.Length != 0)
            throw new ArgumentException("The public web base address must be an HTTP(S) URL ending in a slash, without a query or fragment.");
        this.publicWebBaseUri = publicWebBaseUri;
    }
    public string Build(Guid listId, string? token = null)
        => new Uri(publicWebBaseUri, $"view/{listId}").AbsoluteUri + (token is null ? "" : "#" + Uri.EscapeDataString(token));
}

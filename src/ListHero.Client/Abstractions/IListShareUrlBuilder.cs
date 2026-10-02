namespace ListHero.Client.Abstractions;

// A Hybrid host supplies the public web address instead of its local app URI.
public interface IListShareUrlBuilder
{
    string Build(Guid listId, string? token = null);
}

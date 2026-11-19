namespace ListHero.UI.Components;

public sealed class ShareOptionsDraft
{
    public bool Expires { get; set; }
    public DateTime Expiration { get; set; } = DateTime.UtcNow.AddDays(7);
}

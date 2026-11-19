namespace ListHero.UI.Components;

public sealed record DraftField(string Id, string Value, bool Checked, string Type);
public sealed record RecoveredDraft<T>(T Model, string? EntityId, DraftField[] Fields, int Version = 1)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool RestoreAllowed { get; set; } = true;
}

namespace ListHero.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public bool IsArchived => ArchivedAt.HasValue;

    protected Entity() { }
    protected Entity(DateTimeOffset createdAt) => CreatedAt = createdAt;

    public void Archive(DateTimeOffset archivedAt) => ArchivedAt ??= archivedAt;
}

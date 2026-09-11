namespace FlowOps.Domain.Catalog;

/// <summary>
/// Rule TICKET-ENT-03. CLAUDE.md §4.2 defines <c>Project</c> only as an optional ticket
/// association with no further fields; docs/database.md §5 deliberately adds nothing beyond
/// that. Added in Phase 3A for the same reason as <see cref="Directory.Team"/>.
/// </summary>
public sealed class Project
{
    public int Id { get; }
    public string Name { get; }
    public DateTimeOffset CreatedAt { get; }

    public Project(int id, string name, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        CreatedAt = createdAt;
    }
}

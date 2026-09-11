using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Catalog;

/// <summary>
/// Rule TICKET-ENT-03 / TICKET-INV-02 ("Category belongs to a team, carries a default work
/// type"). Reference data, not an aggregate. Fields match docs/database.md §4 exactly. Added in
/// Phase 3A for the same reason as <see cref="Directory.Team"/>.
/// </summary>
public sealed class Category
{
    public int Id { get; }
    public int TeamId { get; }
    public string Name { get; }
    public WorkType DefaultWorkType { get; }
    public DateTimeOffset CreatedAt { get; }

    public Category(int id, int teamId, string name, WorkType defaultWorkType, DateTimeOffset createdAt)
    {
        Id = id;
        TeamId = teamId;
        Name = name;
        DefaultWorkType = defaultWorkType;
        CreatedAt = createdAt;
    }
}

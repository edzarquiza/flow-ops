namespace FlowOps.Domain.Directory;

/// <summary>
/// Rule TICKET-ENT-03: reference data with a straightforward lifecycle, not an aggregate.
/// Fields match docs/database.md §2 exactly — added in Phase 3A because Phase 2's scope was the
/// Ticket aggregate/SLA/Attention/Authorization only, so this type did not exist yet even though
/// it was already fully specified in docs/database.md and docs/architecture.md.
/// </summary>
public sealed class Team
{
    public int Id { get; }
    public string Name { get; }
    public DateTimeOffset CreatedAt { get; }

    public Team(int id, string name, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        CreatedAt = createdAt;
    }
}

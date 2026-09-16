using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Catalog;

/// <summary>
/// Rule TICKET-ENT-03 / TICKET-INV-02 ("Category belongs to a team, carries a default work
/// type"). Reference data, not an aggregate. Fields match docs/database.md §4 exactly. Added in
/// Phase 3A for the same reason as <see cref="Directory.Team"/>.
/// </summary>
/// <remarks>
/// Phase 22 (ADR-0022): <see cref="IsActive"/> is added so a Category can be deactivated without
/// ever being deleted — existing tickets keep referencing this row and keep displaying its name.
/// Deactivation only removes the category from new ticket creation; it is independent of (and not
/// automatically triggered by) its parent Team's own <see cref="Directory.Team.IsActive"/> — a
/// category under a deactivated team simply stops being *offered* because
/// <c>TicketQueryService.GetCreationOptionsAsync</c> only offers categories under active teams,
/// never because the category row itself was touched. Defaults to <see langword="true"/> so every
/// existing call site keeps constructing active categories.
/// </remarks>
public sealed class Category
{
    public int Id { get; }
    public int TeamId { get; }
    public string Name { get; private set; }
    public WorkType DefaultWorkType { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool IsActive { get; private set; }

    public Category(int id, int teamId, string name, WorkType defaultWorkType, DateTimeOffset createdAt, bool isActive = true)
    {
        Id = id;
        TeamId = teamId;
        Name = name;
        DefaultWorkType = defaultWorkType;
        CreatedAt = createdAt;
        IsActive = isActive;
    }

    public void Rename(string name) => Name = name;

    /// <summary>Terminal: makes the category unavailable for new ticket creation. Never reverses,
    /// never deletes — existing tickets keep their <c>CategoryId</c> and keep displaying this
    /// category's name, since the row itself is untouched.</summary>
    public void Deactivate() => IsActive = false;
}

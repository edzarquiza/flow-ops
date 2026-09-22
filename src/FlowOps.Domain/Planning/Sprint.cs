namespace FlowOps.Domain.Planning;

/// <summary>
/// A lightweight, project-owned planning period (ADR-0029). Not a workflow: it never changes a
/// ticket's <c>Status</c>. Membership lives on the ticket (<c>Ticket.SprintId</c>) so a ticket
/// can belong to at most one sprint by construction, and every membership change is a ticket
/// audit event. Dates are calendar dates (UTC, inclusive) — there is no time-of-day concept.
/// </summary>
public sealed class Sprint
{
    private const int MaxNameLength = 80;

    public int Id { get; private set; }
    public int ProjectId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public SprintStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }

    private Sprint()
    {
    }

    /// <summary>A completed or cancelled sprint is history: it accepts no new tickets (SPRINT-INV-04).</summary>
    public bool AcceptsTickets => Status is SprintStatus.Planned or SprintStatus.Active;

    /// <summary>SPRINT-INV-01/02. New sprints always start Planned.</summary>
    public static Sprint Create(int projectId, string name, DateOnly startDate, DateOnly endDate, DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            throw new DomainRuleException("SPRINT-INV-01", $"Sprint name is required and must be at most {MaxNameLength} characters.");
        }

        if (startDate > endDate)
        {
            throw new DomainRuleException("SPRINT-INV-02", "Sprint start date must be on or before its end date.");
        }

        return new Sprint
        {
            ProjectId = projectId,
            Name = trimmed,
            StartDate = startDate,
            EndDate = endDate,
            Status = SprintStatus.Planned,
            CreatedAt = now,
        };
    }

    /// <summary>SPRINT-INV-03. "At most one Active sprint per project" is a cross-row rule: the
    /// Application layer checks it for a friendly message and a partial unique index enforces it
    /// regardless of code path.</summary>
    public void Activate(DateTimeOffset now)
    {
        if (Status != SprintStatus.Planned)
        {
            throw new DomainRuleException("SPRINT-INV-03", $"Only a planned sprint can be started; this one is {Status}.");
        }

        Status = SprintStatus.Active;
        ActivatedAt = now;
    }

    /// <summary>SPRINT-INV-03. Membership is untouched: unfinished tickets stay attached to the
    /// completed sprint as history (SPRINT-INV-06).</summary>
    public void Complete(DateTimeOffset now)
    {
        if (Status != SprintStatus.Active)
        {
            throw new DomainRuleException("SPRINT-INV-03", $"Only the active sprint can be completed; this one is {Status}.");
        }

        Status = SprintStatus.Completed;
        CompletedAt = now;
    }

    /// <summary>SPRINT-INV-07. Only a Planned sprint that never started can be cancelled; an Active
    /// sprint is completed instead, and a Completed one is history. A cancelled sprint is a read-only
    /// record — never deleted (ADR-0030).</summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status != SprintStatus.Planned)
        {
            throw new DomainRuleException("SPRINT-INV-07", $"Only a planned sprint can be cancelled; this one is {Status}. Complete an active sprint instead.");
        }

        Status = SprintStatus.Cancelled;
        CancelledAt = now;
    }

    public bool Overlaps(DateOnly startDate, DateOnly endDate) => StartDate <= endDate && startDate <= EndDate;
}

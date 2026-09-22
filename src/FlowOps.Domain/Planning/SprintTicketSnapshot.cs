using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Planning;

/// <summary>
/// One ticket's result in one sprint, frozen when the sprint was completed (ADR-0030). The ticket's
/// own <c>SprintId</c> is only its <em>current</em> planning membership and changes when it is carried
/// forward; this row is the historical truth — "this ticket was part of Sprint A, and at completion
/// it was In Progress / Done" — and is never updated or deleted. Written only alongside
/// <see cref="Sprint.Complete"/>, in the same save.
/// </summary>
public sealed class SprintTicketSnapshot
{
    public int SprintId { get; private set; }
    public int TicketId { get; private set; }
    public Status StatusAtCompletion { get; private set; }

    /// <summary>Resolved or Closed at completion — the same terminal test the rest of FlowOps uses.</summary>
    public bool WasDone { get; private set; }

    private SprintTicketSnapshot()
    {
    }

    public SprintTicketSnapshot(int sprintId, int ticketId, Status statusAtCompletion)
    {
        SprintId = sprintId;
        TicketId = ticketId;
        StatusAtCompletion = statusAtCompletion;
        WasDone = statusAtCompletion is Status.Resolved or Status.Closed;
    }
}

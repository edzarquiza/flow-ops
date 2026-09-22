using FlowOps.Domain.Tickets;

namespace FlowOps.Application.Planning;

/// <summary>One existing operation a board move is made of. Every step is an operation that already
/// exists as an explicit action (ADR-0029/0030) — the planner invents no workflow transition.</summary>
public enum BoardStep
{
    PullFromBacklog,

    /// <summary>Clears the sprint-backlog flag when work starts, but only for a caller who may plan
    /// (an Agent starting work is not blocked by lacking planning authority; the flag is
    /// presentation-only and the board column follows the real status regardless).</summary>
    ClearBacklogFlagIfPermitted,
    ReturnToBacklog,
    AssignToCaller,
    StartWork,

    /// <summary>Starts work only if the ticket is Assigned at this point (a resumed ticket may come
    /// back InProgress already).</summary>
    StartWorkIfAssigned,
    Resume,
    PutOnHold,
    Resolve,
    Reopen,
}

/// <summary>The outcome of asking "can this ticket be dragged to that column?". Either an ordered list
/// of existing operations (possibly empty: already there) or a plain-language rejection.</summary>
public sealed record BoardMovePlan(IReadOnlyList<BoardStep> Steps, string? Rejection)
{
    public bool IsAllowed => Rejection is null;

    /// <summary>True when the move needs a reason / resolution the drag alone cannot supply — the UI
    /// asks for it, exactly as the ticket page's own forms do.</summary>
    public bool NeedsInput => Steps.Any(s => s is BoardStep.PutOnHold or BoardStep.Resolve or BoardStep.Reopen);

    public static BoardMovePlan Ok(params BoardStep[] steps) => new(steps, null);

    public static BoardMovePlan Reject(string message) => new([], message);
}

/// <summary>
/// Pure mapping from (current status, backlog flag, target column) to the existing operations that
/// realize a board drag. Deliberately status-derived only: authorization is <em>not</em> decided here
/// — each step is authorized and validated by <c>TicketService</c> with the same policy and domain
/// rule its explicit action uses. Both the server and the page's drop-target highlighting use it, so
/// what the UI offers can never diverge from what the server will attempt.
/// </summary>
public static class BoardMovePlanner
{
    public static BoardMovePlan Plan(Status status, bool sprintBacklog, BoardColumnKey target)
    {
        var current = BoardColumns.For(status, sprintBacklog);
        if (current == target)
        {
            return BoardMovePlan.Ok();
        }

        var notStarted = status is Status.Open or Status.Assigned;

        switch (target)
        {
            case BoardColumnKey.Backlog:
                return notStarted && !sprintBacklog
                    ? BoardMovePlan.Ok(BoardStep.ReturnToBacklog)
                    : BoardMovePlan.Reject("Only an Open or Assigned ticket can be moved back to planned.");

            case BoardColumnKey.Open:
                if (notStarted && sprintBacklog)
                {
                    return BoardMovePlan.Ok(BoardStep.PullFromBacklog);
                }

                return status is Status.Resolved or Status.Closed
                    ? BoardMovePlan.Ok(BoardStep.Reopen)
                    : BoardMovePlan.Reject("Work that has started can't move back to Open.");

            case BoardColumnKey.InProgress:
                if (notStarted)
                {
                    var steps = new List<BoardStep>();
                    if (sprintBacklog)
                    {
                        steps.Add(BoardStep.ClearBacklogFlagIfPermitted);
                    }

                    if (status == Status.Open)
                    {
                        steps.Add(BoardStep.AssignToCaller);
                    }

                    steps.Add(BoardStep.StartWork);
                    return BoardMovePlan.Ok([.. steps]);
                }

                return status == Status.Pending
                    ? BoardMovePlan.Ok(BoardStep.Resume, BoardStep.StartWorkIfAssigned)
                    : BoardMovePlan.Reject("A resolved or closed ticket must be reopened first.");

            case BoardColumnKey.Pending:
                if (status == Status.Open)
                {
                    return BoardMovePlan.Reject("Assign the ticket before putting it on hold.");
                }

                if (status == Status.Assigned)
                {
                    return sprintBacklog
                        ? BoardMovePlan.Ok(BoardStep.ClearBacklogFlagIfPermitted, BoardStep.PutOnHold)
                        : BoardMovePlan.Ok(BoardStep.PutOnHold);
                }

                return status == Status.InProgress
                    ? BoardMovePlan.Ok(BoardStep.PutOnHold)
                    : BoardMovePlan.Reject("A resolved or closed ticket can't be put on hold.");

            case BoardColumnKey.Done:
                return status is Status.InProgress or Status.Pending
                    ? BoardMovePlan.Ok(BoardStep.Resolve)
                    : BoardMovePlan.Reject("Only work in progress or pending can be resolved.");

            default:
                return BoardMovePlan.Reject("Unknown board column.");
        }
    }

    /// <summary>The columns a ticket may be dropped on, for drop-target highlighting.</summary>
    public static IReadOnlyList<BoardColumnKey> AllowedTargets(Status status, bool sprintBacklog) =>
        Enum.GetValues<BoardColumnKey>()
            .Where(target => BoardColumns.For(status, sprintBacklog) != target && Plan(status, sprintBacklog, target).IsAllowed)
            .ToList();
}

/// <summary>The extra text a board move may carry (a hold reason, a resolution, a reopen reason) —
/// the same inputs the ticket page's own forms collect.</summary>
public sealed record BoardMoveInput(string? Reason = null, Resolution? ResolutionCode = null, string? ResolutionNotes = null);

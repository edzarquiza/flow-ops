using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Attention;

/// <summary>
/// Phase 30: the Attention Brief's deterministic "Suggested next step" — explains, never decides.
/// This is deliberately not a second attention policy: it takes the signals
/// <see cref="AttentionPolicy.Evaluate"/> already found (never re-derives whether a signal
/// applies, never reads the <c>Ticket</c> aggregate itself) plus <paramref name="status"/>, a
/// plain already-known fact used only to choose between two pre-written sentences for the same
/// <see cref="AttentionSignalCode.Stalled"/> signal (that one code covers both a Pending ticket
/// stalled too long and an InProgress ticket with no recent activity — two different suggestions
/// for the same detected condition, not two different conditions). No numeric score, no weighing,
/// no new business rule.
/// </summary>
public static class AttentionSuggestion
{
    /// <summary>
    /// Fixed priority order, matching <see cref="AttentionPolicy.Rank"/>'s own severity ordering
    /// (Critical signals first): a ticket with several signals gets the one suggestion most worth
    /// acting on first, not a list of every applicable sentence.
    /// </summary>
    public static string? SuggestNextStep(IReadOnlyList<AttentionSignal> signals, Status status) =>
        SuggestNextStep(signals.Select(s => s.Code).ToList(), status);

    /// <summary>Same rule table as the overload above, taking just the codes — the only field this
    /// method ever reads — so a caller already holding a flattened/DTO signal list (never the
    /// Domain <see cref="AttentionSignal"/> itself) does not need to reconstruct one.</summary>
    public static string? SuggestNextStep(IReadOnlyList<AttentionSignalCode> signalCodes, Status status)
    {
        if (signalCodes.Count == 0)
        {
            return null;
        }

        var codes = signalCodes.ToHashSet();

        if (codes.Contains(AttentionSignalCode.UnassignedUrgent))
        {
            return "Assign an owner.";
        }

        if (codes.Contains(AttentionSignalCode.SlaBreached) || codes.Contains(AttentionSignalCode.Overdue))
        {
            return "Review the current status and update the ticket's next action.";
        }

        if (codes.Contains(AttentionSignalCode.Stalled))
        {
            return status == Status.Pending
                ? "Review the pending reason and determine whether work can resume."
                : "Check whether the ticket is still actively being worked.";
        }

        if (codes.Contains(AttentionSignalCode.SlaAtRisk))
        {
            return "Review remaining work and confirm the ticket has an active owner.";
        }

        // Aging, Churn, and Reopened alone (no more urgent signal present) — none has an example
        // suggestion of its own in the product brief, so this is the one deliberately generic
        // fallback rather than three near-identical invented sentences.
        return "Review this ticket's history and confirm it still has a clear owner and plan.";
    }
}

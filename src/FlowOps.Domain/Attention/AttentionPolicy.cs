using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Attention;

/// <summary>
/// Rule ATTN-RULE-01: the single authoritative implementation of attention-signal detection and
/// ranking. Pure — no I/O; every fact it needs (the ticket, "now", options, the ticket's current
/// SLA risk threshold) is passed in by the caller.
/// </summary>
public static class AttentionPolicy
{
    /// <summary>
    /// Detects every signal that applies to <paramref name="ticket"/> right now. Terminal tickets
    /// (Resolved/Closed) always evaluate to an empty list — confirmed (not merely assumed) in
    /// docs/domain-model.md's "Terminal-ticket resolution" note under ATTN-RULE-02: two signals
    /// state this explicitly, three more are structurally impossible for a terminal ticket
    /// regardless of this guard, and the remaining three are suppressed on the authority of
    /// CLAUDE.md §1/§9/§22 (completed work is not "at-risk work").
    /// </summary>
    public static IReadOnlyList<AttentionSignal> Evaluate(Ticket ticket, DateTimeOffset now, AttentionOptions options, int slaRiskThresholdPercent)
    {
        if (ticket.Status is Status.Resolved or Status.Closed)
        {
            return Array.Empty<AttentionSignal>();
        }

        var signals = new List<AttentionSignal>();

        var slaStatus = SlaPolicy.GetStatus(
            ticket.Status,
            ticket.SlaMet,
            now,
            ticket.SlaStartedAt,
            ticket.SlaDueAt,
            ticket.SlaPausedMinutes,
            ticket.SlaTargetMinutes,
            slaRiskThresholdPercent);

        if (slaStatus == SlaStatus.Breached)
        {
            var overdueBy = now - ticket.SlaDueAt;
            signals.Add(new AttentionSignal(AttentionSignalCode.SlaBreached, AttentionSeverity.Critical, $"SLA breached {Format(overdueBy)} ago", now));
        }
        else if (slaStatus == SlaStatus.AtRisk)
        {
            var remaining = ticket.SlaDueAt - now;
            signals.Add(new AttentionSignal(AttentionSignalCode.SlaAtRisk, AttentionSeverity.High, $"SLA breach in {Format(remaining)}", now));
        }

        if (ticket.DueDate is { } dueDate && dueDate < now)
        {
            signals.Add(new AttentionSignal(AttentionSignalCode.Overdue, AttentionSeverity.High, $"{Format(now - dueDate)} overdue", now));
        }

        if (ticket.AssigneeId is null
            && ticket.Priority is Priority.Critical or Priority.High
            && (now - ticket.CreatedAt).TotalMinutes > options.UnassignedUrgentMinutes)
        {
            signals.Add(new AttentionSignal(AttentionSignalCode.UnassignedUrgent, AttentionSeverity.Critical, $"{ticket.Priority} and unassigned for {Format(now - ticket.CreatedAt)}", now));
        }

        if (options.AgingThresholdDays.TryGetValue(ticket.Priority, out var agingDays)
            && (now - ticket.CreatedAt).TotalDays > agingDays)
        {
            signals.Add(new AttentionSignal(AttentionSignalCode.Aging, AttentionSeverity.Medium, $"Open {(int)(now - ticket.CreatedAt).TotalDays} days", now));
        }

        if (IsStalled(ticket, now, options, out var stalledFor))
        {
            signals.Add(new AttentionSignal(AttentionSignalCode.Stalled, AttentionSeverity.Medium, $"No activity for {Format(stalledFor)}", now));
        }

        if (ticket.AssignmentChangeCount >= options.ChurnAssignmentChangeThreshold)
        {
            signals.Add(new AttentionSignal(AttentionSignalCode.Churn, AttentionSeverity.Medium, $"{ticket.AssignmentChangeCount} reassignment events", now));
        }

        if (ticket.ReopenCount >= 1)
        {
            signals.Add(new AttentionSignal(AttentionSignalCode.Reopened, AttentionSeverity.Medium, ticket.ReopenCount == 1 ? "Reopened once" : $"Reopened {ticket.ReopenCount} times", now));
        }

        return signals;
    }

    /// <summary>
    /// Rule ATTN-RULE-04: highest signal severity, then nearest SlaDueAt, then priority, then
    /// oldest CreatedAt, ties broken by Id. Tickets with no signals are dropped.
    /// </summary>
    public static IReadOnlyList<TicketAttentionResult> Rank(IEnumerable<TicketAttentionResult> candidates) =>
        candidates
            .Where(c => c.Signals.Count > 0)
            .OrderByDescending(c => c.Signals.Max(s => s.Severity))
            .ThenBy(c => c.Ticket.SlaDueAt)
            .ThenByDescending(c => c.Ticket.Priority)
            .ThenBy(c => c.Ticket.CreatedAt)
            .ThenBy(c => c.Ticket.Id)
            .ToList();

    private static bool IsStalled(Ticket ticket, DateTimeOffset now, AttentionOptions options, out TimeSpan stalledFor)
    {
        if (ticket.Status == Status.Pending && ticket.PendingSince is { } pendingSince)
        {
            var elapsed = now - pendingSince;
            if (elapsed.TotalDays > options.StalledPendingDays)
            {
                stalledFor = elapsed;
                return true;
            }
        }
        else if (ticket.Status == Status.InProgress)
        {
            var lastActivity = ticket.Events.Count > 0 ? ticket.Events.Max(e => e.OccurredAt) : ticket.CreatedAt;
            var elapsed = now - lastActivity;
            if (elapsed.TotalDays > options.StalledInProgressDays)
            {
                stalledFor = elapsed;
                return true;
            }
        }

        stalledFor = TimeSpan.Zero;
        return false;
    }

    private static string Format(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{Math.Max(0, (int)span.TotalMinutes)}m";
    }
}

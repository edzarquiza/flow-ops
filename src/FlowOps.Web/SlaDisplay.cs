using FlowOps.Application.Tickets;

namespace FlowOps.Web;

/// <summary>
/// Formats an already-derived SLA view for display. Presentation only — it computes no status and
/// no deadline, it just renders what <see cref="FlowOps.Domain.Sla.SlaPolicy"/> already decided
/// (SLA-RULE-12). CLAUDE.md §22 asks for relative times like "42m left" / "2h 14m over", with the
/// absolute time carried alongside in a title attribute by the caller.
/// </summary>
public static class SlaDisplay
{
    /// <summary>
    /// "42m left" or "2h 14m over", or null for a terminal ticket, whose SLA outcome is a settled
    /// fact rather than a countdown.
    /// </summary>
    public static string? Remaining(TicketSlaView sla)
    {
        if (sla.Remaining is not { } remaining)
        {
            return null;
        }

        return remaining >= TimeSpan.Zero
            ? $"{Format(remaining)} left"
            : $"{Format(remaining.Negate())} over";
    }

    /// <summary>"2h 14m" — the same relative-duration format <see cref="Remaining"/> uses, exposed
    /// for callers with a plain duration rather than a full <see cref="TicketSlaView"/> (Phase 10's
    /// Average Resolution Time KPI).</summary>
    public static string Format(TimeSpan span)
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

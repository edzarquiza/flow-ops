using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Sla;

/// <summary>
/// Rule SLA-RULE-12: the single authoritative implementation of SLA deadline and status
/// calculation. Pure and static — no I/O, no injected services; every fact it needs is passed
/// in by the caller.
/// </summary>
public static class SlaPolicy
{
    /// <summary>SLA-RULE-05.</summary>
    public static DateTimeOffset CalculateDueDate(DateTimeOffset startedAt, int targetMinutes, int pausedMinutes = 0) =>
        startedAt.AddMinutes(targetMinutes + pausedMinutes);

    /// <summary>
    /// SLA-RULE-01 resolution order: exact (WorkType, Priority) match first, then the
    /// (null, Priority) default row. Pure — <paramref name="configurations"/> is an in-memory
    /// collection the caller already loaded; no database access happens here.
    /// </summary>
    /// <remarks>
    /// Returns the whole matched row rather than a single field, because two different callers
    /// need two different values out of the same resolution: ticket creation and reopen want
    /// <see cref="SlaConfiguration.TargetMinutes"/>, while <see cref="GetStatus"/> needs
    /// <see cref="SlaConfiguration.RiskThresholdPercent"/>. Resolving once here keeps SLA-RULE-01's
    /// order in exactly one place (SLA-RULE-12); a caller that only needed the threshold would
    /// otherwise have to re-walk exact-then-default for itself.
    /// </remarks>
    /// <exception cref="DomainRuleException">Neither an exact match nor a default row exists.</exception>
    public static SlaConfiguration ResolveConfiguration(IEnumerable<SlaConfiguration> configurations, WorkType workType, Priority priority)
    {
        SlaConfiguration? exactMatch = null;
        SlaConfiguration? defaultForPriority = null;

        foreach (var configuration in configurations)
        {
            if (configuration.Priority != priority)
            {
                continue;
            }

            if (configuration.WorkType == workType)
            {
                exactMatch = configuration;
            }
            else if (configuration.WorkType is null)
            {
                defaultForPriority = configuration;
            }
        }

        return exactMatch
            ?? defaultForPriority
            ?? throw new DomainRuleException("SLA-RULE-01", $"No SLA configuration resolves for {workType}/{priority}.");
    }

    /// <summary>
    /// SLA-RULE-01's target minutes, resolved by <see cref="ResolveConfiguration"/> — the same
    /// single resolution mechanism, never a second walk of the order.
    /// </summary>
    public static int ResolveTargetMinutes(IEnumerable<SlaConfiguration> configurations, WorkType workType, Priority priority) =>
        ResolveConfiguration(configurations, workType, priority).TargetMinutes;

    /// <summary>
    /// SLA-RULE-10. <paramref name="riskThresholdPercent"/> is read live from the ticket's
    /// current SLA configuration (unlike <c>SlaTargetMinutes</c>, which is frozen on the ticket
    /// at clock-start per SLA-RULE-03) — the rule text places the "target" snapshot but not the
    /// risk threshold on the ticket itself.
    /// </summary>
    public static SlaStatus GetStatus(
        Status ticketStatus,
        bool? slaMet,
        DateTimeOffset now,
        DateTimeOffset slaStartedAt,
        DateTimeOffset slaDueAt,
        int slaPausedMinutes,
        int slaTargetMinutes,
        int riskThresholdPercent)
    {
        if (ticketStatus is Status.Resolved or Status.Closed)
        {
            return slaMet == true ? SlaStatus.Met : SlaStatus.Breached;
        }

        if (ticketStatus == Status.Pending)
        {
            return SlaStatus.Paused;
        }

        if (now >= slaDueAt)
        {
            return SlaStatus.Breached;
        }

        var elapsedMinutes = (now - slaStartedAt).TotalMinutes - slaPausedMinutes;
        var elapsedPercent = slaTargetMinutes == 0 ? 100d : elapsedMinutes / slaTargetMinutes * 100d;

        return elapsedPercent >= riskThresholdPercent ? SlaStatus.AtRisk : SlaStatus.Within;
    }
}

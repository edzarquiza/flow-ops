namespace FlowOps.Domain.Sla;

/// <summary>
/// Rule SLA-RULE-10. Computed only, by <see cref="SlaPolicy.GetStatus"/> — never stored on
/// <see cref="Tickets.Ticket"/> or in any table (see docs/database.md §13, "Derived, not stored").
/// </summary>
public enum SlaStatus
{
    Within,
    AtRisk,
    Paused,
    Breached,
    Met,
}

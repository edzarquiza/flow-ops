namespace FlowOps.Domain.Tickets;

/// <summary>
/// Rule TICKET-ENUM-02. Declared in ascending severity order (Low → Critical) so that ordinal
/// comparison (e.g. <c>OrderByDescending</c>) yields "most urgent first" without extra mapping —
/// an implementation convenience, not a business rule. The rule text's own ordering
/// (Critical | High | Medium | Low) is preserved in this comment for traceability.
/// </summary>
public enum Priority
{
    Low,
    Medium,
    High,
    Critical,
}

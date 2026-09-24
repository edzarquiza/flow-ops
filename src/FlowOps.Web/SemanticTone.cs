namespace FlowOps.Web;

/// <summary>
/// The closed set of semantic hues every badge (status/priority/SLA/role/severity) draws from —
/// reserved for actual meaning (CLAUDE.md §22), never decoration. Each badge family
/// (<see cref="StatusBadgeDisplay"/>, <see cref="PriorityBadgeDisplay"/>, <see cref="RoleBadgeDisplay"/>,
/// <see cref="SlaBadgeDisplay"/>, <see cref="SeverityBadgeDisplay"/>) maps a tone to its own CSS
/// class, so "what does danger look like" is answered by the stylesheet's tokens, not re-derived
/// per call site.
/// </summary>
public enum SemanticTone
{
    Danger,
    Warn,
    Info,
    Ok,
    Teal,
    Violet,
    Muted,
}

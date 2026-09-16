namespace FlowOps.Web;

/// <summary>
/// The closed set of semantic hues every badge (status/priority/SLA/role/severity) draws from —
/// reserved for actual meaning (CLAUDE.md §22), never decoration. Exactly one owner
/// (<see cref="BadgeMarkup"/>) turns a tone into real CSS values, so "what does danger look like"
/// is answered in one place, not re-derived per badge type.
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

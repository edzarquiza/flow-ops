namespace FlowOps.Domain.Planning;

/// <summary>Planned → Active → Completed, or Planned → Cancelled — one direction only (SPRINT-INV-03/07). Persisted as text
/// (ADR-0009). "Current sprint" is simply the one Active sprint of a project — no scheduler ever
/// advances it (ADR-0006/ADR-0029).</summary>
public enum SprintStatus
{
    Planned,
    Active,
    Completed,
    Cancelled,
}

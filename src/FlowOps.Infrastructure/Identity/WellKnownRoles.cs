namespace FlowOps.Infrastructure.Identity;

/// <summary>
/// AUTH-RULE-01's four roles, with fixed ids so they can be seeded deterministically via
/// migration <c>HasData</c> (CLAUDE.md §6.1) — not a "demo data" concept, just the closed set of
/// role rows the RBAC system requires to exist at all, analogous to an enum's fixed member list.
/// </summary>
public static class WellKnownRoles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Agent = "Agent";
    public const string Viewer = "Viewer";

    public static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-0000000000A1");
    public static readonly Guid ManagerId = Guid.Parse("00000000-0000-0000-0000-0000000000A2");
    public static readonly Guid AgentId = Guid.Parse("00000000-0000-0000-0000-0000000000A3");
    public static readonly Guid ViewerId = Guid.Parse("00000000-0000-0000-0000-0000000000A4");
}

using FlowOps.Domain.Tickets;

namespace FlowOps.Application.Demo;

/// <summary>One of CLAUDE.md §14's four named, log-in-able demo personas.</summary>
public sealed record DemoPersona(string DisplayName, string Email, UserRole Role, string Shows);

/// <summary>
/// CLAUDE.md §14's persona table. Shared between <see cref="DemoDataSeeder"/> (which creates these
/// accounts) and the Login page (which displays their credentials for a demo deployment) so the
/// table exists in exactly one place. One persona per role; there are no other demo users.
/// </summary>
public static class DemoPersonas
{
    public static readonly IReadOnlyList<DemoPersona> All =
    [
        new("Demo Admin", "demo.admin@demo.flowops.dev", UserRole.Admin, "Full organization setup: teams, categories, members, projects, sprints"),
        new("Service Desk Manager", "service.desk.manager@demo.flowops.dev", UserRole.Manager, "At-risk queue, sprint planning, board, team workload, SLA performance"),
        new("IT Support Agent", "it.support.agent@demo.flowops.dev", UserRole.Agent, "Personal queue, ticket handling, resolution flow"),
        new("Executive Viewer", "executive.viewer@demo.flowops.dev", UserRole.Viewer, "Read-only analytics and authorization boundaries"),
    ];
}

using FlowOps.Domain.Tickets;

namespace FlowOps.Application.Demo;

/// <summary>One of CLAUDE.md §14's four named, log-in-able demo personas.</summary>
public sealed record DemoPersona(string DisplayName, string Email, UserRole Role, string Shows);

/// <summary>
/// CLAUDE.md §14's persona table, verbatim. Shared between <see cref="DemoDataSeeder"/> (which
/// creates these accounts) and the Login page (which displays their credentials for a demo
/// deployment) so the table exists in exactly one place.
/// </summary>
public static class DemoPersonas
{
    public static readonly IReadOnlyList<DemoPersona> All =
    [
        new("Service Desk Manager", "service.desk.manager@demo.flowops.dev", UserRole.Manager, "At-risk queue, team workload, SLA performance"),
        new("IT Support Agent", "it.support.agent@demo.flowops.dev", UserRole.Agent, "Personal queue, ticket handling, resolution flow"),
        new("Application Support Agent", "application.support.agent@demo.flowops.dev", UserRole.Agent, "Cross-category work, escalation, reassignment"),
        new("Executive Viewer", "executive.viewer@demo.flowops.dev", UserRole.Viewer, "Read-only analytics and authorization boundaries"),
    ];
}

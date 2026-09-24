using FlowOps.Application.Planning;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tests.Tickets;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Catalog;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Planning;

/// <summary>
/// Phase 29D: <see cref="ProjectPlanningQueryService.GetProjectNavOptionsAsync"/> — the sidebar's
/// own minimal project list. Active-only, organization-scoped, ordered by name, and never a
/// vehicle for a cross-organization project to leak.
/// </summary>
[Collection("Postgres")]
public sealed class ProjectNavOptionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public ProjectNavOptionsTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ReturnsOnlyActiveProjects_OrderedByNameAscending()
    {
        await using var context = _fixture.CreateContext();
        var organizationId = await TicketTestData.GetOrganizationIdAsync(context);
        var user = TicketTestData.User(Guid.NewGuid(), UserRole.Admin);

        context.Add(new Project(0, organizationId, "Zebra Rollout", Now));
        context.Add(new Project(0, organizationId, "Alpha Rollout", Now));
        context.Add(new Project(0, organizationId, "Deactivated Rollout", Now, isActive: false));
        await context.SaveChangesAsync();

        var service = new ProjectPlanningQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var options = await service.GetProjectNavOptionsAsync(user);

        var names = options.Where(o => o.Name is "Zebra Rollout" or "Alpha Rollout" or "Deactivated Rollout").Select(o => o.Name).ToList();
        Assert.Equal(["Alpha Rollout", "Zebra Rollout"], names);
    }

    [Fact]
    public async Task NeverReturnsAnotherOrganizationsProject()
    {
        await using var context = _fixture.CreateContext();
        var user = TicketTestData.User(Guid.NewGuid(), UserRole.Admin);

        var (otherOrganizationId, _) = await TicketTestData.AddSecondOrganizationTeamAsync(context);
        context.Add(new Project(0, otherOrganizationId, "Other Org Exclusive Project", Now));
        await context.SaveChangesAsync();

        var service = new ProjectPlanningQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var options = await service.GetProjectNavOptionsAsync(user);

        Assert.DoesNotContain(options, o => o.Name == "Other Org Exclusive Project");
    }

    [Fact]
    public async Task NoActiveProjects_ReturnsAnEmptyList_NotAnError()
    {
        await using var context = _fixture.CreateContext();
        var organizationId = await TicketTestData.GetOrganizationIdAsync(context);
        var user = TicketTestData.User(Guid.NewGuid(), UserRole.Admin);
        context.Add(new Project(0, organizationId, "Only Inactive Project", Now, isActive: false));
        await context.SaveChangesAsync();

        var service = new ProjectPlanningQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var options = await service.GetProjectNavOptionsAsync(user);

        Assert.DoesNotContain(options, o => o.Name == "Only Inactive Project");
    }
}

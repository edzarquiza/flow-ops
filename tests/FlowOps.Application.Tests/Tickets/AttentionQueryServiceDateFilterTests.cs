using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 25 §16: the At-Risk page's date filter — narrower than the Work Queue's, always by
/// <c>SlaDueAt</c>, applied strictly after <see cref="FlowOps.Domain.Attention.AttentionPolicy"/>
/// has already decided eligibility and rank (mirrors how <c>search</c> is already tested in
/// <see cref="AttentionQueryServiceTests"/>). Shares that class's seeding helpers via the same
/// partial class.
/// </summary>
public sealed partial class AttentionQueryServiceTests
{
    [Fact] // §16: narrows an already-at-risk list by SlaDueAt without changing eligibility.
    public async Task GetAtRiskAsync_DueDateRange_NarrowsToTicketsWithSlaDueAtInRange()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // Both breached (SlaDueAt in the past, well before Now) so both are at-risk regardless of
        // any date filter — the only difference this test cares about is where SlaDueAt itself falls.
        var nearId = await CreateTicketAsync(context, world, Now.AddDays(-2), Priority.Medium);
        var farId = await CreateTicketAsync(context, world, Now.AddDays(-40), Priority.Medium);

        var near = await context.Tickets.AsNoTracking().SingleAsync(t => t.Id == nearId);
        var far = await context.Tickets.AsNoTracking().SingleAsync(t => t.Id == farId);
        Assert.True(near.SlaDueAt < Now);
        Assert.True(far.SlaDueAt < near.SlaDueAt);

        // A range that covers only the near ticket's SlaDueAt day.
        var range = new DateRangeFilter(
            DateRangeOption.Custom,
            DateOnly.FromDateTime(near.SlaDueAt.UtcDateTime.Date),
            DateOnly.FromDateTime(near.SlaDueAt.UtcDateTime.Date));

        var unfiltered = await NewService(context).GetAtRiskAsync(world.Agent, 1);
        var filtered = await NewService(context).GetAtRiskAsync(world.Agent, 1, dueDateRange: range);

        Assert.Contains(unfiltered.Items, i => i.Id == nearId);
        Assert.Contains(unfiltered.Items, i => i.Id == farId);

        Assert.Contains(filtered.Items, i => i.Id == nearId);
        Assert.DoesNotContain(filtered.Items, i => i.Id == farId);
    }

    [Fact] // §16: the filter never widens eligibility — a healthy (non-at-risk) ticket whose
           // SlaDueAt happens to fall inside the selected range still does not appear.
    public async Task GetAtRiskAsync_DueDateRange_NeverAdmitsATicketWithNoSignal()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var healthyId = await CreateTicketAsync(context, world, Now.AddMinutes(-5), Priority.Medium);
        var healthy = await context.Tickets.AsNoTracking().SingleAsync(t => t.Id == healthyId);
        Assert.True(healthy.SlaDueAt > Now); // not breached, not at-risk

        var range = new DateRangeFilter(
            DateRangeOption.Custom,
            DateOnly.FromDateTime(healthy.SlaDueAt.UtcDateTime.Date),
            DateOnly.FromDateTime(healthy.SlaDueAt.UtcDateTime.Date));

        var page = await NewService(context).GetAtRiskAsync(world.Agent, 1, dueDateRange: range);

        Assert.DoesNotContain(page.Items, i => i.Id == healthyId);
    }

    [Fact]
    public async Task GetAtRiskAsync_AllTimeDateRange_BehavesExactlyLikeNoFilter()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var breachedId = await CreateTicketAsync(context, world, Now.AddDays(-5), Priority.Medium);

        var withoutFilter = await NewService(context).GetAtRiskAsync(world.Agent, 1);
        var withAllTime = await NewService(context).GetAtRiskAsync(world.Agent, 1, dueDateRange: DateRangeFilter.None);

        Assert.Equal(withoutFilter.TotalCount, withAllTime.TotalCount);
        Assert.Contains(withAllTime.Items, i => i.Id == breachedId);
    }
}

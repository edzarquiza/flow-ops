using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 25 §10/§13: the Work Queue's date-range filter (<see cref="QueueDateField"/> /
/// <see cref="DateRangeFilter"/>) against real PostgreSQL — boundary correctness (a range must
/// include its own end day, never exclude it), each field option, and that widening the date
/// filter never reaches outside the caller's existing authorization scope.
/// </summary>
[Collection("Postgres")]
public sealed class TicketQueryServiceDateFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public TicketQueryServiceDateFilterTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact] // §13/§25: a record created exactly at the range's start instant is included.
    public async Task GetQueueAsync_CreatedExactlyAtFromBoundary_IsIncluded()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);

        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 9, 30);
        var atStartOfFrom = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        await CreateTicketAtAsync(context, teamId, categoryId, user, atStartOfFrom);

        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var range = new DateRangeFilter(DateRangeOption.Custom, from, to);

        var page = await query.GetQueueAsync(user, 1, dateField: QueueDateField.Created, dateRange: range);

        Assert.Equal(1, page.TotalCount);
    }

    [Fact] // §13: the classic off-by-one — a record on the END day itself must not be excluded by
           // an accidental "timestamp <= endOfDay 00:00" boundary.
    public async Task GetQueueAsync_CreatedLateOnToDay_IsIncluded()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);

        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 9, 30);
        var lateOnToDay = new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);

        await CreateTicketAtAsync(context, teamId, categoryId, user, lateOnToDay);

        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var range = new DateRangeFilter(DateRangeOption.Custom, from, to);

        var page = await query.GetQueueAsync(user, 1, dateField: QueueDateField.Created, dateRange: range);

        Assert.Equal(1, page.TotalCount);
    }

    [Fact] // §13/§25: one day outside the range, on either side, is excluded.
    public async Task GetQueueAsync_OneDayOutsideRange_IsExcluded()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);

        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 9, 30);

        await CreateTicketAtAsync(context, teamId, categoryId, user, new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero));
        await CreateTicketAtAsync(context, teamId, categoryId, user, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var range = new DateRangeFilter(DateRangeOption.Custom, from, to);

        var page = await query.GetQueueAsync(user, 1, dateField: QueueDateField.Created, dateRange: range);

        Assert.Equal(0, page.TotalCount);
    }

    [Fact] // §12: From == To is a valid single-day range, not an error.
    public async Task GetQueueAsync_FromEqualsTo_IsASingleDayRange()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);

        var day = new DateOnly(2026, 9, 15);
        await CreateTicketAtAsync(context, teamId, categoryId, user, new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero));
        await CreateTicketAtAsync(context, teamId, categoryId, user, new DateTimeOffset(2026, 9, 16, 0, 1, 0, TimeSpan.Zero));

        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var range = new DateRangeFilter(DateRangeOption.Custom, day, day);

        var page = await query.GetQueueAsync(user, 1, dateField: QueueDateField.Created, dateRange: range);

        Assert.Equal(1, page.TotalCount);
    }

    [Fact] // §25: an empty filtered range returns a correct, non-throwing empty page.
    public async Task GetQueueAsync_NoTicketsInRange_ReturnsEmptyPageNotError()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);
        await CreateTicketAtAsync(context, teamId, categoryId, user, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var range = new DateRangeFilter(DateRangeOption.Custom, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31));

        var page = await query.GetQueueAsync(user, 1, dateField: QueueDateField.Created, dateRange: range);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact] // §10: the Due field option filters by DueDate, not CreatedAt.
    public async Task GetQueueAsync_DueField_FiltersByDueDateNotCreatedAt()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);

        // Created far outside the filtered range, but due inside it.
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var dueInRange = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(createdAt), TestEmail.Sender, TestEmail.Options);
        await service.CreateAsync(
            Request(teamId, categoryId) with { DueDate = dueInRange },
            user);

        var range = new DateRangeFilter(DateRangeOption.Custom, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var byDue = await query.GetQueueAsync(user, 1, dateField: QueueDateField.Due, dateRange: range);
        var byCreated = await query.GetQueueAsync(user, 1, dateField: QueueDateField.Created, dateRange: range);

        Assert.Equal(1, byDue.TotalCount);
        Assert.Equal(0, byCreated.TotalCount); // created in January — outside the September range
    }

    [Fact] // §10: the PlannedStart field option filters by PlannedStartDate; a ticket with none
           // never matches any range but AllTime.
    public async Task GetQueueAsync_PlannedStartField_ExcludesTicketsWithNoPlannedStart()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);

        await service.CreateAsync(Request(teamId, categoryId), user); // no planned start at all

        var range = new DateRangeFilter(DateRangeOption.Custom, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var page = await query.GetQueueAsync(user, 1, dateField: QueueDateField.PlannedStart, dateRange: range);

        Assert.Equal(0, page.TotalCount);
    }

    [Fact] // §21: widening the date filter can never reach outside the caller's existing
           // authorization scope — a ticket in a genuinely different organization, in the exact
           // same date range, must not leak.
    public async Task GetQueueAsync_DateFilterNeverCrossesOrganizationBoundary()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);
        await CreateTicketAtAsync(context, teamId, categoryId, user, Now);

        var (otherOrganizationId, otherTeamId) = await TicketTestData.AddSecondOrganizationTeamAsync(context);
        var otherCategoryId = await TicketTestData.AddCategoryAsync(context, otherTeamId);
        var otherUserId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, otherTeamId, otherUserId);
        var otherUser = TicketTestData.UserInOrganization(otherOrganizationId, otherUserId, UserRole.Admin, otherTeamId);
        await CreateTicketAtAsync(context, otherTeamId, otherCategoryId, otherUser, Now);

        var range = new DateRangeFilter(DateRangeOption.Custom, DateOnly.FromDateTime(Now.Date), DateOnly.FromDateTime(Now.Date));
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var page = await query.GetQueueAsync(user, 1, dateField: QueueDateField.Created, dateRange: range);

        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public void DateRangeFilter_InvalidCustomRange_IsFlaggedNotSilentlySwapped()
    {
        var invalid = new DateRangeFilter(DateRangeOption.Custom, new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 1));
        Assert.True(invalid.IsInvalidCustomRange);
        Assert.Null(invalid.Resolve(Now)); // never silently swapped into a valid reversed range
    }

    [Fact]
    public void DateRangeFilter_AllTime_ResolvesToNoFilter()
    {
        Assert.Null(DateRangeFilter.None.Resolve(Now));
    }

    private static async Task CreateTicketAtAsync(FlowOpsDbContext context, int teamId, int categoryId, CurrentUser user, DateTimeOffset createdAt)
    {
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(createdAt), TestEmail.Sender, TestEmail.Options);
        await service.CreateAsync(Request(teamId, categoryId), user);
    }

    private static CreateTicketRequest Request(int teamId, int categoryId) =>
        new(
            Title: "Printer on 3rd floor is jammed",
            Description: "The printer near the east stairwell is jammed and needs a technician.",
            WorkType: WorkType.Incident,
            Priority: Priority.Medium,
            TeamId: teamId,
            CategoryId: categoryId,
            ProjectId: null);

    /// <summary>
    /// An Agent scoped to only this test's own team — not Admin. All Application.Tests in this
    /// collection share one process-lifetime Organization and Postgres container (see
    /// <see cref="TicketTestData"/>'s own doc comment), so an Admin's org-wide view would include
    /// every other concurrently-running test's tickets too; scoping to a single, uniquely-named
    /// team is what actually isolates this test's assertions.
    /// </summary>
    private static async Task<(int TeamId, int CategoryId, CurrentUser User)> SeedAsync(FlowOpsDbContext context)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);
        var userId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, userId);
        return (teamId, categoryId, TicketTestData.User(userId, UserRole.Agent, teamId));
    }
}

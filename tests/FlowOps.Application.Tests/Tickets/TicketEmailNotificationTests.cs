using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Email;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 30 (ADR-0035): the two remaining transactional emails — ticket assignment/reassignment
/// and new comments — proven against real PostgreSQL with a <see cref="RecordingEmailSender"/> so
/// each test can inspect exactly what would have gone out, with no network call.
/// </summary>
[Collection("Postgres")]
public sealed class TicketEmailNotificationTests
{
    private static readonly DateTimeOffset Start = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public TicketEmailNotificationTests(PostgresFixture fixture) => _fixture = fixture;

    // ---------------------------------------------------------------------------------------
    // Assignment / reassignment
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AssignAsync_SendsEmailToTheNewAssignee()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);

        var sent = Assert.Single(world.EmailSender.SentMessages);
        Assert.Equal(world.AgentEmail, sent.ToEmail);
        Assert.Contains("assigned", sent.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // Self-assign is never emailed — the actor already knows what they just did.
    public async Task AssignAsync_SelfAssign_SendsNoEmail()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AssignAsync(world.TicketId, world.Agent.UserId, world.Agent);

        Assert.Empty(world.EmailSender.SentMessages);
    }

    [Fact]
    public async Task ReassignAsync_SendsEmailToTheNewAssignee_NotThePrevious()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);
        world.EmailSender.Clear();

        await world.Service.ReassignAsync(world.TicketId, world.SecondAgentId, world.Manager);

        var sent = Assert.Single(world.EmailSender.SentMessages);
        Assert.Equal(world.SecondAgentEmail, sent.ToEmail);
        Assert.NotEqual(world.AgentEmail, sent.ToEmail);
    }

    [Fact] // Same self-notification exclusion applies on reassignment.
    public async Task ReassignAsync_ToSelf_SendsNoEmail()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);

        await world.Service.ReassignAsync(world.TicketId, world.Manager.UserId, world.Manager);

        Assert.DoesNotContain(world.EmailSender.SentMessages, m => m.ToEmail != world.AgentEmail);
    }

    [Fact] // Failure semantics: a failed send never rolls back the already-committed assignment.
    public async Task AssignAsync_EmailDeliveryFails_AssignmentStillPersists()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        world.EmailSender.FailNextSends = true;

        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);

        await using var verify = _fixture.CreateContext();
        var ticket = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == world.TicketId);
        Assert.Equal(world.AgentId, ticket.AssigneeId);
        Assert.Empty(world.EmailSender.SentMessages);
    }

    // ---------------------------------------------------------------------------------------
    // Comments
    // ---------------------------------------------------------------------------------------

    [Fact] // Requester and assignee both get the email; the author (the assignee here) is excluded.
    public async Task AddCommentAsync_NotifiesRequesterAndAssignee_ExcludingTheAuthor()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);
        world.EmailSender.Clear();

        await world.Service.AddCommentAsync(world.TicketId, "Looking into this now.", isInternal: false, world.Agent);

        var sent = Assert.Single(world.EmailSender.SentMessages);
        Assert.Equal(world.RequesterEmail, sent.ToEmail);
        Assert.Contains("comment", sent.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // Requester == assignee collapses to exactly one email, never two.
    public async Task AddCommentAsync_SameRequesterAndAssignee_SendsExactlyOneEmail()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.RequesterId, world.Manager);
        world.EmailSender.Clear();

        await world.Service.AddCommentAsync(world.TicketId, "Update from the manager.", isInternal: false, world.Manager);

        var sent = Assert.Single(world.EmailSender.SentMessages);
        Assert.Equal(world.RequesterEmail, sent.ToEmail);
    }

    [Fact] // Internal comments are withheld from a Viewer (TicketAccessPolicy.CanSeeInternalComments)
           // — the assignee (an Agent) is not a Viewer, so they are still notified.
    public async Task AddCommentAsync_Internal_ExcludesTheViewerRequesterButNotOtherRecipients()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedViewerRequesterAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);
        world.EmailSender.Clear();

        await world.Service.AddCommentAsync(world.TicketId, "Internal note.", isInternal: true, world.Manager);

        var sent = Assert.Single(world.EmailSender.SentMessages);
        Assert.Equal(world.AgentEmail, sent.ToEmail);
        Assert.DoesNotContain(world.EmailSender.SentMessages, m => m.ToEmail == world.RequesterEmail);
    }

    [Fact] // The same Viewer requester is still notified for a public (non-internal) comment.
    public async Task AddCommentAsync_Public_StillNotifiesAViewerRequester()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedViewerRequesterAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);
        world.EmailSender.Clear();

        await world.Service.AddCommentAsync(world.TicketId, "Public update.", isInternal: false, world.Manager);

        Assert.Contains(world.EmailSender.SentMessages, m => m.ToEmail == world.RequesterEmail);
        Assert.Equal(2, world.EmailSender.SentMessages.Count); // Viewer requester + Agent assignee
    }

    [Fact] // A deactivated recipient never receives an email.
    public async Task AddCommentAsync_DeactivatedAssignee_IsExcluded()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);
        world.EmailSender.Clear();

        var assigneeUser = await context.Users.SingleAsync(u => u.Id == world.AgentId);
        assigneeUser.IsActive = false;
        await context.SaveChangesAsync();

        await world.Service.AddCommentAsync(world.TicketId, "Comment after deactivation.", isInternal: false, world.Requester);

        Assert.Empty(world.EmailSender.SentMessages);
    }

    [Fact] // Two comments never produce a duplicate email to the same recipient within one call.
    public async Task AddCommentAsync_NeverSendsDuplicateEmailsToTheSameRecipient()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);
        world.EmailSender.Clear();

        await world.Service.AddCommentAsync(world.TicketId, "One update.", isInternal: false, world.Agent);

        var recipients = world.EmailSender.SentMessages.Select(m => m.ToEmail).ToList();
        Assert.Equal(recipients.Distinct().Count(), recipients.Count);
    }

    [Fact] // Failure semantics: a failed send never rolls back the already-committed comment.
    public async Task AddCommentAsync_EmailDeliveryFails_CommentStillPersists()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);
        world.EmailSender.Clear();
        world.EmailSender.FailNextSends = true;

        await world.Service.AddCommentAsync(world.TicketId, "Comment despite a failing email provider.", isInternal: false, world.Agent);

        await using var verify = _fixture.CreateContext();
        var commentCount = await verify.TicketEvents.CountAsync(e => e.TicketId == world.TicketId && e.EventType == TicketEventType.CommentAdded);
        Assert.Equal(1, commentCount);
        Assert.Empty(world.EmailSender.SentMessages);
    }

    // ---------- seeding ----------

    private sealed record World(
        int TicketId,
        int TeamId,
        Guid AgentId,
        Guid SecondAgentId,
        Guid RequesterId,
        string AgentEmail,
        string SecondAgentEmail,
        string RequesterEmail,
        CurrentUser Agent,
        CurrentUser Requester,
        CurrentUser Manager,
        TicketService Service,
        RecordingEmailSender EmailSender);

    private static async Task<World> SeedAsync(FlowOpsDbContext context)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);
        var organizationId = await TicketTestData.GetOrganizationIdAsync(context);

        var agentId = await TicketTestData.AddUserAsync(context, "Agent One");
        var secondAgentId = await TicketTestData.AddUserAsync(context, "Agent Two");
        var requesterId = await TicketTestData.AddUserAsync(context, "Requester");
        var managerId = await TicketTestData.AddUserAsync(context, "Manager");

        await TicketTestData.AddTeamMembershipAsync(context, teamId, agentId);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, secondAgentId);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, requesterId);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, managerId, isTeamManager: true);

        // Comment-email recipient resolution joins Users to OrganizationMembership — every
        // seeded user needs one, same as CurrentUserAccessorTests' own real-Identity setup.
        context.Add(new OrganizationMembership(0, organizationId, agentId, UserRole.Agent, Start));
        context.Add(new OrganizationMembership(0, organizationId, secondAgentId, UserRole.Agent, Start));
        context.Add(new OrganizationMembership(0, organizationId, requesterId, UserRole.Agent, Start));
        context.Add(new OrganizationMembership(0, organizationId, managerId, UserRole.Manager, Start));
        await context.SaveChangesAsync();

        var agentEmail = await EmailAsync(context, agentId);
        var secondAgentEmail = await EmailAsync(context, secondAgentId);
        var requesterEmail = await EmailAsync(context, requesterId);

        var clock = new TicketTestData.FixedTimeProvider(Start);
        var emailSender = new RecordingEmailSender();
        var service = new TicketService(context, clock, emailSender, TestEmail.Options);
        var requester = TicketTestData.User(requesterId, UserRole.Agent, teamId);

        var (ticketId, _) = await service.CreateAsync(
            new CreateTicketRequest(
                Title: "Printer on 3rd floor is jammed",
                Description: "The printer near the east stairwell is jammed and needs a technician.",
                WorkType: WorkType.Incident,
                Priority: Priority.Medium,
                TeamId: teamId,
                CategoryId: categoryId,
                ProjectId: null),
            requester);

        return new World(
            ticketId,
            teamId,
            agentId,
            secondAgentId,
            requesterId,
            agentEmail,
            secondAgentEmail,
            requesterEmail,
            TicketTestData.User(agentId, UserRole.Agent, teamId),
            requester,
            TicketTestData.Manager(managerId, teamId),
            service,
            emailSender);
    }

    /// <summary>Same as <see cref="SeedAsync"/>, but the requester is a Viewer — a role
    /// <see cref="TicketAccessPolicy.CanSeeInternalComments"/> excludes.</summary>
    private static async Task<World> SeedViewerRequesterAsync(FlowOpsDbContext context)
    {
        var world = await SeedAsync(context);
        var organizationId = await TicketTestData.GetOrganizationIdAsync(context);

        var membership = await context.OrganizationMemberships
            .SingleAsync(m => m.OrganizationId == organizationId && m.UserId == world.RequesterId);
        context.Remove(membership);
        context.Add(new OrganizationMembership(0, organizationId, world.RequesterId, UserRole.Viewer, Start));
        await context.SaveChangesAsync();

        return world with { Requester = TicketTestData.User(world.RequesterId, UserRole.Viewer, world.TeamId) };
    }

    private static async Task<string> EmailAsync(FlowOpsDbContext context, Guid userId) =>
        (await context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).SingleAsync())!;
}

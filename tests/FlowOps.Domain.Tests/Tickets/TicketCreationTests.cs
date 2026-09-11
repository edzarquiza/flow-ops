using FlowOps.Domain;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using Xunit;
using static FlowOps.Domain.Tests.Tickets.TicketTestFactory;

namespace FlowOps.Domain.Tests.Tickets;

public class TicketCreationTests
{
    [Theory] // TICKET-INV-01 — title boundaries: 4 invalid, 5 valid, 200 valid, 201 invalid
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void Create_TitleLengthBoundaries_EnforcedExactly(int titleLength, bool expectSuccess)
    {
        var title = new string('a', titleLength);

        void Act() => Ticket.Create(title, "A description long enough to be valid.", WorkType.Incident,
            Priority.Medium, RequesterId, TeamId, CategoryId, TeamId, null, 1440, Now);

        if (expectSuccess)
        {
            var ticket = Ticket.Create(title, "A description long enough to be valid.", WorkType.Incident,
                Priority.Medium, RequesterId, TeamId, CategoryId, TeamId, null, 1440, Now);
            Assert.Equal(title, ticket.Title);
        }
        else
        {
            var ex = Assert.Throws<DomainRuleException>(Act);
            Assert.Equal("TICKET-INV-01", ex.RuleCode);
        }
    }

    [Theory] // TICKET-INV-01 — description boundaries: 8000 valid, 8001 invalid
    [InlineData(8000, true)]
    [InlineData(8001, false)]
    public void Create_DescriptionLengthBoundaries_EnforcedExactly(int descriptionLength, bool expectSuccess)
    {
        var description = new string('a', descriptionLength);

        if (expectSuccess)
        {
            var ticket = Ticket.Create("Valid title here", description, WorkType.Incident, Priority.Medium,
                RequesterId, TeamId, CategoryId, TeamId, null, 1440, Now);
            Assert.Equal(description, ticket.Description);
        }
        else
        {
            var ex = Assert.Throws<DomainRuleException>(() => Ticket.Create("Valid title here", description,
                WorkType.Incident, Priority.Medium, RequesterId, TeamId, CategoryId, TeamId, null, 1440, Now));
            Assert.Equal("TICKET-INV-01", ex.RuleCode);
        }
    }

    [Fact] // TICKET-INV-02
    public void Create_CategoryNotBelongingToTeam_Throws()
    {
        var ex = Assert.Throws<DomainRuleException>(() => Ticket.Create("Valid title here",
            "A description long enough to be valid.", WorkType.Incident, Priority.Medium, RequesterId,
            teamId: 1, CategoryId, categoryTeamId: 2, null, 1440, Now));

        Assert.Equal("TICKET-INV-02", ex.RuleCode);
    }

    [Fact] // TICKET-ENT-01 / TICKET-INV-09 / AUDIT-RULE-02 / AUDIT-RULE-03
    public void Create_AppendsExactlyOneCreatedEvent()
    {
        var ticket = CreateOpenTicket();

        var single = Assert.Single(ticket.Events);
        Assert.Equal(TicketEventType.Created, single.EventType);
        Assert.Equal(RequesterId, single.ActorUserId);
        Assert.Equal(Now, single.OccurredAt);
    }

    [Fact] // TICKET-INV-10 — every timestamp comes from the caller-supplied "now", not the clock
    public void Create_UsesSuppliedNow_NotSystemClock()
    {
        var fixedNow = new DateTimeOffset(2030, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var ticket = CreateOpenTicket(now: fixedNow);

        Assert.Equal(fixedNow, ticket.CreatedAt);
        Assert.Equal(fixedNow, ticket.UpdatedAt);
        Assert.Equal(fixedNow, ticket.SlaStartedAt);
        Assert.Equal(fixedNow, ticket.Events.Single().OccurredAt);
    }

    [Fact] // SLA-RULE-05
    public void Create_SetsSlaDueAt_ToStartPlusTarget()
    {
        var ticket = CreateOpenTicket(slaTargetMinutes: 480);
        Assert.Equal(Now.AddMinutes(480), ticket.SlaDueAt);
    }

    [Fact] // SLA-RULE-04
    public void Create_InitializesSlaFields()
    {
        var ticket = CreateOpenTicket();
        Assert.Equal(0, ticket.SlaPausedMinutes);
        Assert.Null(ticket.PendingSince);
        Assert.Null(ticket.SlaMet);
        Assert.Equal(Status.Open, ticket.Status);
        Assert.Equal(0, ticket.ReopenCount);
        Assert.Equal(0, ticket.AssignmentChangeCount);
    }
}

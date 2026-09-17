using FlowOps.Domain;
using FlowOps.Domain.Tickets;
using Xunit;
using static FlowOps.Domain.Tests.Tickets.TicketTestFactory;

namespace FlowOps.Domain.Tests.Tickets;

/// <summary>Phase 25 / TICKET-INV-11: planned start and due date are independent, optional
/// planning facts — the only constraint is their relative ordering when both are set.</summary>
public class TicketPlanningDatesTests
{
    [Fact]
    public void Create_NoDates_Valid()
    {
        var ticket = Ticket.Create("Valid title here", "A description long enough to be valid.",
            WorkType.Incident, Priority.Medium, RequesterId, TeamId, CategoryId, TeamId, null, 1440, Now);

        Assert.Null(ticket.PlannedStartDate);
        Assert.Null(ticket.DueDate);
    }

    [Fact]
    public void Create_StartOnly_Valid()
    {
        var start = Now.AddDays(1);

        var ticket = Ticket.Create("Valid title here", "A description long enough to be valid.",
            WorkType.Incident, Priority.Medium, RequesterId, TeamId, CategoryId, TeamId, null, 1440, Now,
            plannedStartDate: start);

        Assert.Equal(start, ticket.PlannedStartDate);
        Assert.Null(ticket.DueDate);
    }

    [Fact]
    public void Create_DueOnly_Valid()
    {
        var due = Now.AddDays(3);

        var ticket = Ticket.Create("Valid title here", "A description long enough to be valid.",
            WorkType.Incident, Priority.Medium, RequesterId, TeamId, CategoryId, TeamId, null, 1440, Now,
            dueDate: due);

        Assert.Null(ticket.PlannedStartDate);
        Assert.Equal(due, ticket.DueDate);
    }

    [Fact]
    public void Create_StartBeforeDue_Valid()
    {
        var start = Now.AddDays(1);
        var due = Now.AddDays(3);

        var ticket = Ticket.Create("Valid title here", "A description long enough to be valid.",
            WorkType.Incident, Priority.Medium, RequesterId, TeamId, CategoryId, TeamId, null, 1440, Now,
            plannedStartDate: start, dueDate: due);

        Assert.Equal(start, ticket.PlannedStartDate);
        Assert.Equal(due, ticket.DueDate);
    }

    [Fact]
    public void Create_StartEqualsDue_Valid()
    {
        var same = Now.AddDays(2);

        var ticket = Ticket.Create("Valid title here", "A description long enough to be valid.",
            WorkType.Incident, Priority.Medium, RequesterId, TeamId, CategoryId, TeamId, null, 1440, Now,
            plannedStartDate: same, dueDate: same);

        Assert.Equal(same, ticket.PlannedStartDate);
        Assert.Equal(same, ticket.DueDate);
    }

    [Fact] // TICKET-INV-11
    public void Create_StartAfterDue_Rejected()
    {
        var start = Now.AddDays(3);
        var due = Now.AddDays(1);

        var ex = Assert.Throws<DomainRuleException>(() => Ticket.Create("Valid title here",
            "A description long enough to be valid.", WorkType.Incident, Priority.Medium, RequesterId,
            TeamId, CategoryId, TeamId, null, 1440, Now, plannedStartDate: start, dueDate: due));

        Assert.Equal("TICKET-INV-11", ex.RuleCode);
    }

    [Fact] // TICKET-INV-11 is not an SLA rule — SlaDueAt/SlaStartedAt are computed exactly as
           // before, unaffected by whatever planning dates are supplied.
    public void Create_PlanningDates_DoNotAffectSla()
    {
        var ticket = Ticket.Create("Valid title here", "A description long enough to be valid.",
            WorkType.Incident, Priority.Medium, RequesterId, TeamId, CategoryId, TeamId, null, 1440, Now,
            plannedStartDate: Now.AddDays(30), dueDate: Now.AddDays(60));

        Assert.Equal(Now, ticket.SlaStartedAt);
        Assert.Equal(Now.AddMinutes(1440), ticket.SlaDueAt);
    }
}

using FlowOps.Domain.Tickets;
using Xunit;

namespace FlowOps.Domain.Tests.Tickets;

/// <summary>Guards TICKET-ENUM-01..04 and AUTH-RULE-01 against silent drift.</summary>
public class EnumExhaustivenessTests
{
    [Fact] // TICKET-ENUM-01
    public void WorkType_HasExactlyFourValues()
    {
        var values = Enum.GetValues<WorkType>();
        Assert.Equal(4, values.Length);
        Assert.Equal(new[] { WorkType.Incident, WorkType.ServiceRequest, WorkType.Task, WorkType.Problem }, values);
    }

    [Fact] // TICKET-ENUM-02
    public void Priority_HasExactlyFourValues()
    {
        Assert.Equal(4, Enum.GetValues<Priority>().Length);
    }

    [Fact] // TICKET-ENUM-03
    public void Status_HasExactlySixValues()
    {
        var values = Enum.GetValues<Status>();
        Assert.Equal(6, values.Length);
        Assert.Equal(new[] { Status.Open, Status.Assigned, Status.InProgress, Status.Pending, Status.Resolved, Status.Closed }, values);
    }

    [Fact] // TICKET-ENUM-04
    public void Resolution_HasExactlySixValues()
    {
        Assert.Equal(6, Enum.GetValues<Resolution>().Length);
    }

    [Fact] // AUTH-RULE-01
    public void UserRole_HasExactlyFourValues()
    {
        var values = Enum.GetValues<UserRole>();
        Assert.Equal(4, values.Length);
        Assert.Equal(new[] { UserRole.Admin, UserRole.Manager, UserRole.Agent, UserRole.Viewer }, values);
    }

    [Fact] // AUDIT-RULE-03
    public void TicketEventType_HasExactlySeventeenValues()
    {
        Assert.Equal(17, Enum.GetValues<TicketEventType>().Length); // +SprintChanged (ADR-0029)
    }
}

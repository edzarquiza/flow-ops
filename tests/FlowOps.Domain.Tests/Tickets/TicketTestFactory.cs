using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Tests.Tickets;

/// <summary>Shared fixture data for Ticket tests — deterministic time, fixed ids, no randomness.</summary>
internal static class TicketTestFactory
{
    public static readonly DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    public const int TeamId = 1;
    public const int CategoryId = 10;
    public static readonly Guid RequesterId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid AssigneeId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public static readonly Guid ManagerId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public static readonly Guid OtherAgentId = Guid.Parse("00000000-0000-0000-0000-000000000004");

    public static Ticket CreateOpenTicket(
        Priority priority = Priority.Medium,
        int slaTargetMinutes = 1440,
        DateTimeOffset? now = null) =>
        Ticket.Create(
            title: "Printer on 3rd floor is jammed",
            description: "The printer near the east stairwell is jammed and needs a technician.",
            workType: WorkType.Incident,
            priority: priority,
            requesterId: RequesterId,
            teamId: TeamId,
            categoryId: CategoryId,
            categoryTeamId: TeamId,
            projectId: null,
            slaTargetMinutes: slaTargetMinutes,
            now: now ?? Now);

    public static Ticket CreateAssignedTicket(Priority priority = Priority.Medium, int slaTargetMinutes = 1440, DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? Now;
        var ticket = CreateOpenTicket(priority, slaTargetMinutes, effectiveNow);
        ticket.Assign(AssigneeId, assigneeIsActiveTeamMember: true, actorUserId: ManagerId, effectiveNow);
        return ticket;
    }

    public static Ticket CreateInProgressTicket(Priority priority = Priority.Medium, int slaTargetMinutes = 1440, DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? Now;
        var ticket = CreateAssignedTicket(priority, slaTargetMinutes, effectiveNow);
        ticket.StartWork(new TicketActor(AssigneeId, UserRole.Agent), effectiveNow);
        return ticket;
    }

    public static SlaConfiguration[] DefaultSlaConfigurations() =>
    [
        new SlaConfiguration(1, null, Priority.Critical, 240, 80),
        new SlaConfiguration(2, null, Priority.High, 480, 80),
        new SlaConfiguration(3, null, Priority.Medium, 1440, 80),
        new SlaConfiguration(4, null, Priority.Low, 4320, 80),
    ];
}

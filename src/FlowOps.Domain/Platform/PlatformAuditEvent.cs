namespace FlowOps.Domain.Platform;

/// <summary>
/// Phase 24 (ADR-0023): the closed set of platform-level lifecycle actions worth recording. Deliberately
/// small — this is not a general audit framework, only the mutations Phase 24/24A actually
/// introduce. A new platform mutation that needs auditing adds a new member here, not a new table.
/// </summary>
public enum PlatformEventType
{
    OrganizationDeactivated,
    OrganizationReactivated,
    UserDeactivated,
    UserReactivated,

    /// <summary>Phase 24A (ADR-0024): a Platform Admin approved a Pending account, granting it its
    /// first-ever ability to authenticate.</summary>
    UserApproved,

    /// <summary>Phase 24A-Extension (ADR-0025): a Platform Admin rejected a Pending account — it
    /// never gains the ability to authenticate.</summary>
    UserRejected,
}

/// <summary>
/// One platform-administration action, for accountability. Deliberately separate from
/// <see cref="FlowOps.Domain.Tickets.TicketEvent"/>, which is scoped to one ticket by a required foreign key and
/// has no way to represent an organization- or user-level action with no ticket involved — forcing
/// this into <c>TicketEvent</c> would mean either a fake/nullable ticket reference or overloading
/// its schema for a concept it was never designed to hold. Exactly one of
/// <see cref="TargetOrganizationId"/>/<see cref="TargetUserId"/> is set, matching
/// <see cref="EventType"/>; never both.
/// </summary>
public sealed class PlatformAuditEvent
{
    public int Id { get; }
    public PlatformEventType EventType { get; }
    public Guid ActorUserId { get; }
    public int? TargetOrganizationId { get; }
    public Guid? TargetUserId { get; }
    public DateTimeOffset OccurredAt { get; }

    public PlatformAuditEvent(
        int id,
        PlatformEventType eventType,
        Guid actorUserId,
        int? targetOrganizationId,
        Guid? targetUserId,
        DateTimeOffset occurredAt)
    {
        Id = id;
        EventType = eventType;
        ActorUserId = actorUserId;
        TargetOrganizationId = targetOrganizationId;
        TargetUserId = targetUserId;
        OccurredAt = occurredAt;
    }
}

namespace FlowOps.Domain.Tickets;

/// <summary>Rule TICKET-ENUM-03.</summary>
public enum Status
{
    Open,
    Assigned,
    InProgress,
    Pending,
    Resolved,
    Closed,
}

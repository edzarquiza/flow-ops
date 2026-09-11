namespace FlowOps.Domain.Tickets;

/// <summary>Rule TICKET-ENUM-04.</summary>
public enum Resolution
{
    Fixed,
    Completed,
    WorkaroundProvided,
    NoFaultFound,
    Duplicate,
    Withdrawn,
}

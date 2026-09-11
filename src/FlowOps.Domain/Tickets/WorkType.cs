namespace FlowOps.Domain.Tickets;

/// <summary>Rule TICKET-ENUM-01. Exactly four values per ADR-0003 — do not add more.</summary>
public enum WorkType
{
    Incident,
    ServiceRequest,
    Task,
    Problem,
}

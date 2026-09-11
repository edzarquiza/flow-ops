namespace FlowOps.Domain.Tickets;

/// <summary>Rule AUTH-RULE-01. Exactly four roles, one primary role per user.</summary>
public enum UserRole
{
    Admin,
    Manager,
    Agent,
    Viewer,
}

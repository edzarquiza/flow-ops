namespace FlowOps.Application.Tickets;

/// <summary>
/// Raised when <see cref="FlowOps.Domain.Tickets.TicketAccessPolicy"/> denies an operation the
/// caller attempted. Distinct from <see cref="FlowOps.Domain.DomainRuleException"/> on purpose:
/// a domain-rule violation is a 422 (the request was understood but breaks a business rule),
/// whereas this is a 403 (CLAUDE.md §11.3). Without the distinction, Web could not map the two
/// outcomes to different responses without re-deciding authorization for itself.
/// </summary>
public sealed class TicketAccessDeniedException : Exception
{
    public TicketAccessDeniedException(string message)
        : base(message)
    {
    }
}

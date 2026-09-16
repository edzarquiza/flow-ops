namespace FlowOps.Application.Organizations;

/// <summary>
/// Raised when <see cref="FlowOps.Domain.Organizations.OrganizationAccessPolicy"/> denies an
/// invitation or member-management operation. The organization-scoped counterpart of
/// <see cref="FlowOps.Application.Tickets.TicketAccessDeniedException"/> — same 403 rationale
/// (CLAUDE.md §11.3), kept as its own type rather than reused across both bounded concerns so a
/// future change to one policy's error handling can never silently change the other's.
/// </summary>
public sealed class OrganizationAccessDeniedException(string message) : Exception(message);

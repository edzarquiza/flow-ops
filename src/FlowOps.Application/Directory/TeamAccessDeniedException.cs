namespace FlowOps.Application.Directory;

/// <summary>
/// Raised when <see cref="FlowOps.Domain.Directory.DirectoryAccessPolicy"/> denies a team-management
/// operation. Distinct from a plain validation failure (surfaced instead as
/// <see cref="CreateTeamResult"/>'s <c>Error</c>) for the same reason
/// <see cref="FlowOps.Application.Tickets.TicketAccessDeniedException"/> is: this is a 403 (the
/// caller may not do this at all), not a 422 (the request was understood but rejected).
/// </summary>
public sealed class TeamAccessDeniedException : Exception
{
    public TeamAccessDeniedException(string message)
        : base(message)
    {
    }
}

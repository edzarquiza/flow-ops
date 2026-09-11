namespace FlowOps.Domain.Directory;

/// <summary>
/// Rule TICKET-ENT-03 / CLAUDE.md §4.2 ("TeamMember (user↔team, with the team's manager
/// flagged)"). Composite-keyed join between a team and a user; fields match docs/database.md §3
/// exactly. Added in Phase 3A for the same reason as <see cref="Team"/>.
/// </summary>
public sealed class TeamMember
{
    public int TeamId { get; }
    public Guid UserId { get; }
    public bool IsTeamManager { get; }
    public DateTimeOffset JoinedAt { get; }

    public TeamMember(int teamId, Guid userId, bool isTeamManager, DateTimeOffset joinedAt)
    {
        TeamId = teamId;
        UserId = userId;
        IsTeamManager = isTeamManager;
        JoinedAt = joinedAt;
    }
}

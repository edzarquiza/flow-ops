using FlowOps.Application.Tickets;

namespace FlowOps.Web;

/// <summary>
/// ADR-0026: the single place that knows first-run setup step order, display labels, target
/// pages, and which steps are skippable — shared by the dashboard's own checklist
/// (<c>Pages/Index.cshtml</c>) and the "next step" banner the four setup-related pages render
/// (<c>_SetupNextStep.cshtml</c>), so both always agree on ordering without duplicating it.
/// </summary>
public static class SetupSteps
{
    public sealed record Step(string Key, string Label, string PageUrl, bool Skippable);

    public static readonly IReadOnlyList<Step> All =
    [
        new("team", "Set up your first team", "/Admin/Index", Skippable: false),
        new("invite", "Invite your team", "/Organization/Members", Skippable: true),
        new("project", "Create your first project", "/Admin/Projects/Index", Skippable: true),
        new("ticket", "Create your first ticket", "/Tickets/Create", Skippable: false),
    ];

    public static bool IsStepDone(string key, WorkspaceSetupStatus status) => key switch
    {
        "team" => status.HasTeam,
        "invite" => status.InviteComplete,
        "project" => status.ProjectComplete,
        "ticket" => status.HasTicket,
        _ => true,
    };

    /// <summary>The first step (in <see cref="All"/> order) that is not yet done — <see langword="null"/>
    /// once every step is done, the same "nothing left to say" state that hides the dashboard's own
    /// panel entirely (<see cref="WorkspaceSetupStatus.IsComplete"/>).</summary>
    public static Step? FirstIncomplete(WorkspaceSetupStatus status) =>
        All.FirstOrDefault(s => !IsStepDone(s.Key, status));
}

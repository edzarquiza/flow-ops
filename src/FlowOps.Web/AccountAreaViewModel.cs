using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;

namespace FlowOps.Web;

/// <summary>
/// Everything the shared _AccountArea partial needs — rendered twice by the layout (the desktop
/// top-right toolbar, and a mobile copy inside the off-canvas sidebar), since CSS alone cannot
/// move one DOM node between two different structural parents across breakpoints without
/// JavaScript (CLAUDE.md §11.1: none). Both renders read the same already-resolved data; nothing
/// here is computed twice.
/// </summary>
public sealed record AccountAreaViewModel(
    CurrentUser? CurrentUser,
    string Email,
    IReadOnlyList<OrganizationOption> AvailableOrganizations,
    string? CurrentOrganizationName);

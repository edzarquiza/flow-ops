using FlowOps.Domain.Tickets;

namespace FlowOps.Web;

/// <summary>
/// Presentation-only mapping from a <see cref="Resolution"/> to product-facing copy — the same
/// space-before-capitals humanizing <see cref="WorkTypeDisplay"/> already applies to
/// <see cref="WorkType"/>, extended to Resolution's own multi-word values ("WorkaroundProvided",
/// "NoFaultFound"). Decides nothing: the value actually submitted by the Resolve form is always
/// the enum's own name, never this text.
/// </summary>
public static class ResolutionDisplay
{
    public static string ToDisplayName(Resolution resolution) => resolution switch
    {
        Resolution.WorkaroundProvided => "Workaround Provided",
        Resolution.NoFaultFound => "No Fault Found",
        var r => r.ToString(),
    };
}

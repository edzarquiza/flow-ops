using FlowOps.Domain.Tickets;

namespace FlowOps.Web;

/// <summary>
/// Presentation-only mapping from a <see cref="WorkType"/> to product-facing copy — spacing out
/// "ServiceRequest" as "Service Request" wherever this is read as prose (the dashboard's
/// resolution-time comparison, the work-type filter's own option text). Decides nothing: the
/// underlying value submitted by a filter/form is always the enum's own name, never this text.
/// </summary>
public static class WorkTypeDisplay
{
    public static string ToDisplayName(WorkType workType) => workType switch
    {
        WorkType.ServiceRequest => "Service Request",
        var w => w.ToString(),
    };
}

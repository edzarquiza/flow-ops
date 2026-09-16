namespace FlowOps.Web;

/// <summary>
/// Phase 24A (CSP stabilization): the CSP's <c>style-src 'self'</c> has no <c>'unsafe-inline'</c>,
/// which blocks the <c>style</c> attribute on any element — including the inline
/// <c>style="width:37.2%"</c> every bar chart (Dashboard's hbar rows, the workspace-setup
/// progress bar) previously used to draw a data-driven proportion. A width this small does not
/// need sub-percent precision to read correctly, so it is rounded to the nearest whole percent and
/// expressed as one of 101 pre-generated <c>.w-pct-0</c>..<c>.w-pct-100</c> utility classes
/// instead — CSP-safe with no loosening of the policy, and no perceptible loss of accuracy (a bar
/// a few tenths of a percent off is not visually distinguishable on any chart in this app).
/// </summary>
public static class CssWidthClass
{
    public static string For(double percent) => $"w-pct-{Round(percent)}";

    public static string For(int percent) => For((double)percent);

    /// <summary>The workflow rail's own current-stop fill — same rounding, same CSP reasoning,
    /// a differently-named class family (<c>.rail-fill-0</c>..<c>.rail-fill-100</c>) because it
    /// sets the rail's <c>--rail-fill-pct</c> custom property rather than <c>width</c>.</summary>
    public static string ForRailFill(double fraction) => $"rail-fill-{Round(fraction * 100)}";

    private static int Round(double percent) =>
        (int)Math.Round(Math.Clamp(percent, 0, 100), MidpointRounding.AwayFromZero);
}

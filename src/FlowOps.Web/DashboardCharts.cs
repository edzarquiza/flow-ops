using System.Globalization;
using FlowOps.Application.Tickets;

namespace FlowOps.Web;

/// <summary>
/// Phase 20 (ADR-0019): presentation-only geometry for the dashboard's charts — like
/// <see cref="WorkflowRail"/> and <see cref="TimelinePresentation"/>, this computes no data, only
/// where an already-computed value falls on a chart's fixed drawing surface. No charting library:
/// the trend line is a single inline <c>&lt;polyline&gt;</c> point string, and every bar chart is
/// plain CSS width/height percentages — both degrade to the accessible table/list markup rendered
/// alongside them when CSS or JavaScript is unavailable.
/// </summary>
public static class DashboardCharts
{
    public const int TrendWidth = 600;
    public const int TrendHeight = 130;
    private const int TrendPadding = 8;
    private const int TrendBottomPadding = 24;

    /// <summary>The chart's own drawable band — top inset <see cref="TrendPadding"/>, bottom inset
    /// <see cref="TrendBottomPadding"/> (room for the axis date labels beneath the line itself, so
    /// they read as part of the chart rather than floating text under it).</summary>
    private static double DrawableHeight => TrendHeight - TrendPadding - TrendBottomPadding;

    /// <summary>Y of the chart's zero baseline — where the axis labels sit just below.</summary>
    public static double BaselineY => TrendPadding + DrawableHeight;

    /// <summary>Y of the single interior gridline, at the midpoint of the drawable band — a quiet
    /// scale reference, not a full grid (a busy grid would fight the line for attention).</summary>
    public static double MidlineY => TrendPadding + (DrawableHeight / 2);

    /// <summary>
    /// One "x,y" pair per <paramref name="points"/> entry, ready to drop straight into a
    /// <c>&lt;polyline points="..."&gt;</c>. Y is inverted (SVG's origin is top-left) and scaled
    /// against the series' own maximum count, never a hard-coded scale, so a quiet week and a busy
    /// one both use the chart's full height. A single point (or an all-zero series) renders as a
    /// flat line at the baseline rather than dividing by zero.
    /// </summary>
    public static string PolylinePoints(IReadOnlyList<DashboardTrendPoint> points) =>
        string.Join(" ", Coordinates(points).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.X:F1},{c.Y:F1}")));

    /// <summary>The last point's own coordinate — where the chart draws its one marker, so the
    /// current/most-recent value is easy to find without hunting along the line.</summary>
    public static (double X, double Y) LastPoint(IReadOnlyList<DashboardTrendPoint> points) => Coordinates(points)[^1];

    private static IReadOnlyList<(double X, double Y)> Coordinates(IReadOnlyList<DashboardTrendPoint> points)
    {
        if (points.Count == 0)
        {
            return [];
        }

        var max = points.Max(p => p.Count);
        var drawableWidth = TrendWidth - (2 * TrendPadding);

        var coordinates = new (double X, double Y)[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var x = points.Count == 1
                ? TrendPadding + (drawableWidth / 2d)
                : TrendPadding + (drawableWidth * i / (double)(points.Count - 1));
            var fraction = max == 0 ? 0d : points[i].Count / (double)max;
            var y = TrendPadding + (DrawableHeight * (1 - fraction));
            coordinates[i] = (x, y);
        }

        return coordinates;
    }

    /// <summary>Bar length as a percentage of the series' own maximum — 0 when every value in the
    /// series is 0, so an empty period never divides by zero into <c>NaN%</c>.</summary>
    public static double BarPercent(int value, int seriesMax) => seriesMax <= 0 ? 0 : Math.Clamp(value * 100d / seriesMax, 0, 100);

    /// <summary>Phase 24A (CSP stabilization): <see cref="BarPercent"/> rounded to the one of the
    /// 101 pre-generated <c>.w-pct-0</c>..<c>.w-pct-100</c> classes (see <see cref="CssWidthClass"/>
    /// and flowops.css's own comment) — <c>style-src 'self'</c> blocks the <c>style</c> attribute
    /// a raw percentage would otherwise need.</summary>
    public static string WidthClass(int value, int seriesMax) => CssWidthClass.For(BarPercent(value, seriesMax));
}

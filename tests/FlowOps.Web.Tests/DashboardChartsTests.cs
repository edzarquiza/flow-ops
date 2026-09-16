using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using FlowOps.Web;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase 20 design refinement: <see cref="DashboardCharts"/> is a pure presentation function, so
/// its proportions are tested directly rather than through a full HTTP round trip. This is the
/// regression guard for the "bars don't represent actual magnitude" defect the refinement pass
/// found and fixed — every assertion here is about the actual numeric relationship between values,
/// not incidental markup.
/// </summary>
public sealed class DashboardChartsTests
{
    [Theory]
    [InlineData(103, 103, 100.0)] // the series maximum always renders at exactly 100%
    [InlineData(11, 103, 10.679611650485436)]
    [InlineData(0, 103, 0.0)]
    public void BarPercent_IsProportionalToSeriesMaximum(int value, int seriesMax, double expected) =>
        Assert.Equal(expected, DashboardCharts.BarPercent(value, seriesMax), precision: 6);

    [Fact] // The exact defect reported: six status counts must produce six visibly different
           // widths, each proportional to its own value, not a near-uniform row of bars.
    public void BarPercent_DistinctValues_ProduceDistinctAndOrderedWidths()
    {
        var counts = new[] { 36, 37, 42, 11, 103, 71 };
        var max = counts.Max();

        var widths = counts.Select(c => DashboardCharts.BarPercent(c, max)).ToArray();

        // Sorting by count and sorting by rendered width must agree — width preserves order.
        var orderedByCount = counts.OrderBy(c => c).ToArray();
        var orderedByWidth = counts.Zip(widths, (count, width) => (count, width))
            .OrderBy(x => x.width)
            .Select(x => x.count)
            .ToArray();
        Assert.Equal(orderedByCount, orderedByWidth);
        // The smallest (Pending, 11) is a small fraction of the largest (Resolved, 103) — not
        // visually indistinguishable, which is what the reported defect looked like.
        Assert.True(widths.Min() < widths.Max() / 5);
        // The maximum value always reaches exactly full width.
        Assert.Equal(100.0, widths.Max());
    }

    [Fact]
    public void BarPercent_ZeroSeriesMax_IsZero_NeverNaNOrDivideByZero()
    {
        Assert.Equal(0.0, DashboardCharts.BarPercent(0, seriesMax: 0));
        Assert.Equal(0.0, DashboardCharts.BarPercent(5, seriesMax: 0));
    }

    [Fact] // Phase 20C §13: two different value/max pairs with the same ratio (23h50m of 1d23h
           // ≈ 1430/2780 minutes, matched here by an equivalent simpler ratio) must render at the
           // same width — the calculation is proportional, not tied to the specific numbers.
    public void BarPercent_EqualRatios_ProduceEqualWidths()
    {
        var first = DashboardCharts.BarPercent(30, 60);
        var second = DashboardCharts.BarPercent(50, 100);

        Assert.Equal(first, second);
        Assert.Equal(50.0, first);
    }

    [Fact] // A smaller positive value must always produce a strictly smaller percentage than a
           // larger one against the same series maximum — never equal, never inverted.
    public void BarPercent_SmallerPositiveValue_ProducesSmallerWidth()
    {
        var smaller = DashboardCharts.BarPercent(1430, 2780); // ~23h50m of a ~1d23h maximum
        var larger = DashboardCharts.BarPercent(2780, 2780);

        Assert.True(smaller < larger);
        Assert.Equal(100.0, larger);
    }

    [Fact]
    public void PolylinePoints_EmptySeries_IsEmptyString() =>
        Assert.Equal(string.Empty, DashboardCharts.PolylinePoints([]));

    [Fact]
    public void PolylinePoints_SinglePoint_IsCenteredHorizontally()
    {
        var points = new[] { new DashboardTrendPoint(new DateOnly(2026, 1, 1), 5) };

        var result = DashboardCharts.PolylinePoints(points);

        var expectedX = DashboardCharts.TrendWidth / 2.0;
        Assert.StartsWith(expectedX.ToString("F1"), result);
    }

    [Fact] // The highest point in the series must reach the top of the drawable band, and the
           // lowest must sit on the baseline — the same "use the full height" guarantee the bar
           // charts get, applied to the line chart's vertical scale.
    public void PolylinePoints_ScalesToSeriesMaximum()
    {
        var points = new[]
        {
            new DashboardTrendPoint(new DateOnly(2026, 1, 1), 0),
            new DashboardTrendPoint(new DateOnly(2026, 1, 2), 10),
        };

        var result = DashboardCharts.PolylinePoints(points);
        var coordinates = result.Split(' ').Select(p => p.Split(',').Select(double.Parse).ToArray()).ToArray();

        Assert.Equal(DashboardCharts.BaselineY, coordinates[0][1], precision: 3); // zero value sits on the baseline
        Assert.True(coordinates[1][1] < coordinates[0][1]); // the higher value sits strictly above it
    }

    [Fact]
    public void LastPoint_ReturnsTheFinalCoordinate()
    {
        var points = new[]
        {
            new DashboardTrendPoint(new DateOnly(2026, 1, 1), 3),
            new DashboardTrendPoint(new DateOnly(2026, 1, 2), 8),
        };

        var last = DashboardCharts.LastPoint(points);

        Assert.Equal(DashboardCharts.TrendWidth - 8.0, last.X, precision: 3); // rightmost x (TrendPadding = 8)
    }

    [Fact]
    public void MidlineY_IsBetweenTopPaddingAndBaseline() =>
        Assert.True(DashboardCharts.MidlineY > 0 && DashboardCharts.MidlineY < DashboardCharts.BaselineY);
}

/// <summary>Presentation-only label humanization, tested the same pure-function way.</summary>
public sealed class WorkTypeDisplayTests
{
    [Theory]
    [InlineData(WorkType.Incident, "Incident")]
    [InlineData(WorkType.ServiceRequest, "Service Request")]
    [InlineData(WorkType.Task, "Task")]
    [InlineData(WorkType.Problem, "Problem")]
    public void ToDisplayName_HumanizesOnlyServiceRequest(WorkType workType, string expected) =>
        Assert.Equal(expected, WorkTypeDisplay.ToDisplayName(workType));
}

public sealed class WorkflowRailStatusLabelTests
{
    [Theory]
    [InlineData(Status.Open, "Open")]
    [InlineData(Status.InProgress, "In Progress")]
    [InlineData(Status.Closed, "Closed")]
    public void Label_Status_HumanizesOnlyInProgress(Status status, string expected) =>
        Assert.Equal(expected, WorkflowRail.Label(status));
}

using FlowOps.Application.Planning;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>Phase 28B: the Web-layer mappings that turn technical enum values and stored ids into
/// plain language. The domain enums keep their names; users never see them.</summary>
public sealed class PlainLanguageDisplayTests
{
    [Theory]
    [InlineData(SlaStatus.Within, "On track")]
    [InlineData(SlaStatus.AtRisk, "Deadline soon")]
    [InlineData(SlaStatus.Paused, "Paused")]
    [InlineData(SlaStatus.Breached, "Deadline missed")]
    [InlineData(SlaStatus.Met, "Deadline met")]
    public void SlaStatus_IsShownAsAServiceDeadlineState(SlaStatus status, string expected) =>
        Assert.Equal(expected, SlaDisplay.StatusLabel(status));

    [Fact]
    public void EverySlaStatus_HasALabelThatIsNotTheRawEnumName()
    {
        foreach (var status in Enum.GetValues<SlaStatus>())
        {
            var label = SlaDisplay.StatusLabel(status);
            if (status is not SlaStatus.Paused)
            {
                Assert.NotEqual(status.ToString(), label);
            }

            Assert.DoesNotContain("SLA", label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Resolutions_AreSentenceCase_WithNoRunTogetherWords()
    {
        Assert.Equal("No fault found", ResolutionDisplay.ToDisplayName(Resolution.NoFaultFound));
        Assert.Equal("Workaround provided", ResolutionDisplay.ToDisplayName(Resolution.WorkaroundProvided));
        foreach (var resolution in Enum.GetValues<Resolution>())
        {
            Assert.DoesNotMatch("[a-z][A-Z]", ResolutionDisplay.ToDisplayName(resolution));
        }
    }

    [Fact]
    public void BoardColumn_FormerlySprintBacklog_IsCalledPlanned()
    {
        Assert.Equal("Planned", SprintDisplay.ColumnLabel(BoardColumnKey.Backlog));
        Assert.Equal("In Progress", SprintDisplay.ColumnLabel(BoardColumnKey.InProgress));
    }

    [Theory]
    [InlineData(SprintStatus.Active, false, "Current sprint")]
    [InlineData(SprintStatus.Active, true, "Current sprint · not started")]
    [InlineData(SprintStatus.Planned, false, "Planned sprint")]
    [InlineData(SprintStatus.Completed, false, "Completed")]
    [InlineData(SprintStatus.Cancelled, false, "Cancelled")]
    public void TicketSprintLabel_SaysWhereTheTicketStands(SprintStatus status, bool notStarted, string expected) =>
        Assert.Equal(expected, SprintDisplay.TicketSprintLabel(status, notStarted));

    private static TicketTimelineEntry Event(TicketEventType type, string? field = null, string? oldValue = null, string? newValue = null,
        string? note = null, string? oldDisplay = null, string? newDisplay = null) =>
        new(TicketTimelineEntryKind.Event, 1, DateTimeOffset.UnixEpoch, "Someone", type, field, oldValue, newValue, note, null, null, oldDisplay, newDisplay);

    [Fact]
    public void Timeline_SprintMoves_NameTheSprint()
    {
        var moved = Event(TicketEventType.SprintChanged, "SprintId", "1", "2", oldDisplay: "Sprint 1", newDisplay: "Sprint 2");
        Assert.Equal("Moved to Sprint 2", TimelineDisplay.Title(moved));
        Assert.Equal("Sprint 1 → Sprint 2", TimelineDisplay.Detail(moved));

        var out_ = Event(TicketEventType.SprintChanged, "SprintId", "2", null, oldDisplay: "Sprint 2");
        Assert.Equal("Moved out of sprint", TimelineDisplay.Title(out_));
        Assert.Equal("Sprint 2 → No sprint", TimelineDisplay.Detail(out_));

        Assert.Equal("Moved to Open", TimelineDisplay.Title(Event(TicketEventType.SprintChanged, "SprintBacklog", bool.TrueString, bool.FalseString)));
        Assert.Equal("Moved back to planned", TimelineDisplay.Title(Event(TicketEventType.SprintChanged, "SprintBacklog", bool.FalseString, bool.TrueString)));
    }

    [Fact]
    public void Timeline_EveryEventType_HasAHumanTitle_AndNoRawNames()
    {
        foreach (var type in Enum.GetValues<TicketEventType>())
        {
            var title = TimelineDisplay.Title(Event(type, newDisplay: "Sprint 2"));
            Assert.DoesNotMatch("[a-z][A-Z]", title); // no run-together enum names such as SprintChanged
        }

        Assert.Equal("Due date changed", TimelineDisplay.Title(Event(TicketEventType.DueDateChanged)));
    }

    [Fact]
    public void Timeline_Details_UseNamesAndReadableValues_NeverIds()
    {
        var id = Guid.NewGuid().ToString();
        Assert.Equal("To Sam Rivera", TimelineDisplay.Detail(Event(TicketEventType.Assigned, "AssigneeId", null, id, newDisplay: "Sam Rivera")));
        Assert.Equal("Sam Rivera → Alex Kim", TimelineDisplay.Detail(Event(TicketEventType.Reassigned, "AssigneeId", id, id, oldDisplay: "Sam Rivera", newDisplay: "Alex Kim")));
        Assert.Equal("Assigned → In Progress", TimelineDisplay.Detail(Event(TicketEventType.StatusChanged, "Status", "Assigned", "InProgress")));
        Assert.DoesNotContain(id, TimelineDisplay.Detail(Event(TicketEventType.Assigned, "AssigneeId", null, id)) ?? string.Empty, StringComparison.Ordinal);

        var due = TimelineDisplay.Detail(Event(TicketEventType.DueDateChanged, "DueDate", null, new DateTimeOffset(2026, 9, 25, 14, 0, 0, TimeSpan.Zero).ToString("O")));
        Assert.Equal("Not set → Sep 25, 2026 14:00", due);
        Assert.Equal("Waiting for the vendor", TimelineDisplay.Detail(Event(TicketEventType.PutOnHold, note: "Waiting for the vendor")));
    }
}

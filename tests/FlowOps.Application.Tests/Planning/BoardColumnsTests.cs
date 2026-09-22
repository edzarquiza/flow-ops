using FlowOps.Application.Planning;
using FlowOps.Domain.Tickets;
using Xunit;

namespace FlowOps.Application.Tests.Planning;

/// <summary>The board is Backlog | Open | In progress | Pending | Done — Assigned is not a column of
/// its own (an assigned ticket sits in Open), and Resolved/Closed both read as Done.</summary>
public class BoardColumnsTests
{
    [Theory]
    [InlineData(Status.Open, false, BoardColumnKey.Open)]
    [InlineData(Status.Assigned, false, BoardColumnKey.Open)]
    [InlineData(Status.Open, true, BoardColumnKey.Backlog)]
    [InlineData(Status.Assigned, true, BoardColumnKey.Backlog)]
    [InlineData(Status.InProgress, false, BoardColumnKey.InProgress)]
    [InlineData(Status.InProgress, true, BoardColumnKey.InProgress)] // the flag never overrides real work in flight
    [InlineData(Status.Pending, false, BoardColumnKey.Pending)]
    [InlineData(Status.Resolved, false, BoardColumnKey.Done)]
    [InlineData(Status.Closed, false, BoardColumnKey.Done)]
    public void For_MapsExistingStatusToBoardColumn(Status status, bool backlog, BoardColumnKey expected) =>
        Assert.Equal(expected, BoardColumns.For(status, backlog));

    [Fact]
    public void Board_HasExactlyFiveColumns() =>
        Assert.Equal(
            [BoardColumnKey.Backlog, BoardColumnKey.Open, BoardColumnKey.InProgress, BoardColumnKey.Pending, BoardColumnKey.Done],
            Enum.GetValues<BoardColumnKey>());
}

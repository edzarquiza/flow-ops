using FlowOps.Domain;
using FlowOps.Domain.Tickets;
using Xunit;
using static FlowOps.Domain.Tests.Tickets.TicketTestFactory;

namespace FlowOps.Domain.Tests.Tickets;

public class TicketCommentTests
{
    [Fact] // TICKET-INV-09 (comment half) / AUDIT-RULE-03 CommentAdded
    public void AddComment_AppendsCommentAndExactlyOneEvent()
    {
        var ticket = CreateOpenTicket();
        var eventsBefore = ticket.Events.Count;

        var comment = ticket.AddComment(RequesterId, "Any update on this?", isInternal: false, Now);

        Assert.Single(ticket.Comments);
        Assert.Same(comment, ticket.Comments.Single());
        Assert.Equal(eventsBefore + 1, ticket.Events.Count);
        Assert.Equal(TicketEventType.CommentAdded, ticket.Events.Last().EventType);
    }

    [Fact] // TICKET-ENT-05
    public void AddComment_CanBeMarkedInternal()
    {
        var ticket = CreateOpenTicket();
        var comment = ticket.AddComment(AssigneeId, "Internal note: escalate to vendor.", isInternal: true, Now);
        Assert.True(comment.IsInternal);
    }

    [Fact]
    public void AddComment_WithEmptyBody_Throws()
    {
        var ticket = CreateOpenTicket();
        var ex = Assert.Throws<DomainRuleException>(() => ticket.AddComment(RequesterId, "   ", false, Now));
        Assert.Equal("TICKET-ENT-05", ex.RuleCode);
    }
}

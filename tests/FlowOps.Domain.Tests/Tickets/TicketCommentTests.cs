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

    // Phase F1-B (hardening): comment body was previously unbounded — same TICKET-ENT-05 rule code,
    // same 8000-character bound Description already uses (TICKET-INV-01).
    [Fact]
    public void AddComment_BelowMaximumLength_Succeeds()
    {
        var ticket = CreateOpenTicket();
        var comment = ticket.AddComment(RequesterId, new string('a', 7999), isInternal: false, Now);
        Assert.Equal(7999, comment.Body.Length);
    }

    [Fact]
    public void AddComment_AtMaximumLength_Succeeds()
    {
        var ticket = CreateOpenTicket();
        var comment = ticket.AddComment(RequesterId, new string('a', 8000), isInternal: false, Now);
        Assert.Equal(8000, comment.Body.Length);
    }

    [Fact]
    public void AddComment_AboveMaximumLength_Throws()
    {
        var ticket = CreateOpenTicket();
        var ex = Assert.Throws<DomainRuleException>(() => ticket.AddComment(RequesterId, new string('a', 8001), isInternal: false, Now));
        Assert.Equal("TICKET-ENT-05", ex.RuleCode);
    }
}

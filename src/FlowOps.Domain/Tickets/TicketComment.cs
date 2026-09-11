namespace FlowOps.Domain.Tickets;

/// <summary>
/// Rule TICKET-ENT-05. A child of the <see cref="Ticket"/> aggregate — never constructed
/// standalone; only <see cref="Ticket.AddComment"/> creates one.
/// </summary>
public sealed class TicketComment
{
    public int Id { get; private set; }
    public int TicketId { get; private set; }
    public Guid AuthorId { get; }
    public string Body { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool IsInternal { get; }

    internal TicketComment(Guid authorId, string body, bool isInternal, DateTimeOffset createdAt)
    {
        AuthorId = authorId;
        Body = body;
        IsInternal = isInternal;
        CreatedAt = createdAt;
    }
}

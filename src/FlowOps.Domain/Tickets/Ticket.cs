using FlowOps.Domain.Sla;

namespace FlowOps.Domain.Tickets;

/// <summary>
/// The Ticket aggregate root (TICKET-ENT-01). All mutation happens through the methods on this
/// class (TICKET-ENT-02); nothing else may set <see cref="Status"/>, <see cref="AssigneeId"/>,
/// <see cref="SlaDueAt"/>, or append a <see cref="TicketEvent"/>. Time is always supplied by the
/// caller as an explicit <see cref="DateTimeOffset"/> parameter — this class never calls
/// <c>DateTime.UtcNow</c> (TICKET-INV-10); the caller is expected to source it from an injected
/// <c>TimeProvider</c> at the Application boundary.
/// </summary>
public sealed class Ticket
{
    private const int MinTitleLength = 5;
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 8000;
    private const int MinResolutionNotesLength = 10;

    private readonly List<TicketComment> _comments = new();
    private readonly List<TicketEvent> _events = new();

    /// <summary>Status the ticket was in immediately before entering Pending — needed so
    /// <see cref="Resume"/> can return to the correct prior status (TICKET-WF-05 / TICKET-WF-06).</summary>
    private Status? _statusBeforePending;

    public int Id { get; private set; }
    public string? Reference { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public WorkType WorkType { get; private set; }
    public Priority Priority { get; private set; }
    public Status Status { get; private set; }
    public Guid RequesterId { get; private set; }
    public Guid? AssigneeId { get; private set; }
    public int TeamId { get; private set; }
    public int? ProjectId { get; private set; }
    public int CategoryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DueDate { get; private set; }
    public int SlaTargetMinutes { get; private set; }
    public DateTimeOffset SlaStartedAt { get; private set; }
    public DateTimeOffset SlaDueAt { get; private set; }
    public int SlaPausedMinutes { get; private set; }
    public DateTimeOffset? PendingSince { get; private set; }
    public bool? SlaMet { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public Resolution? ResolutionCode { get; private set; }
    public string? ResolutionNotes { get; private set; }
    public int ReopenCount { get; private set; }
    public int AssignmentChangeCount { get; private set; }
    public string? PendingReason { get; private set; }

    public IReadOnlyCollection<TicketComment> Comments => _comments;
    public IReadOnlyCollection<TicketEvent> Events => _events;

    private Ticket()
    {
    }

    /// <summary>
    /// Creates a new ticket in <see cref="Status.Open"/>. The caller (Application layer) is
    /// responsible for confirming <paramref name="categoryTeamId"/> — the team the category
    /// actually belongs to — since looking that up is I/O the Domain does not perform itself.
    /// </summary>
    public static Ticket Create(
        string title,
        string description,
        WorkType workType,
        Priority priority,
        Guid requesterId,
        int teamId,
        int categoryId,
        int categoryTeamId,
        int? projectId,
        int slaTargetMinutes,
        DateTimeOffset now)
    {
        ValidateTitle(title);
        ValidateDescription(description);

        if (categoryTeamId != teamId)
        {
            throw new DomainRuleException("TICKET-INV-02", "The category must belong to the ticket's team.");
        }

        var ticket = new Ticket
        {
            Title = title,
            Description = description,
            WorkType = workType,
            Priority = priority,
            Status = Status.Open,
            RequesterId = requesterId,
            TeamId = teamId,
            CategoryId = categoryId,
            ProjectId = projectId,
            CreatedAt = now,
            UpdatedAt = now,
            SlaTargetMinutes = slaTargetMinutes,
            SlaStartedAt = now,
            SlaDueAt = SlaPolicy.CalculateDueDate(now, slaTargetMinutes),
            SlaPausedMinutes = 0,
            ReopenCount = 0,
            AssignmentChangeCount = 0,
        };

        ticket.AppendEvent(TicketEventType.Created, requesterId, now, field: null, oldValue: null, newValue: null, note: null);
        return ticket;
    }

    /// <summary>TICKET-WF-01. <paramref name="assigneeIsActiveTeamMember"/> is resolved by the
    /// caller (Application layer) since Domain performs no I/O (TICKET-INV-03).</summary>
    public void Assign(Guid assigneeId, bool assigneeIsActiveTeamMember, Guid actorUserId, DateTimeOffset now)
    {
        RequireStatus(Status.Open, "TICKET-WF-01", nameof(Assign));

        if (!assigneeIsActiveTeamMember)
        {
            throw new DomainRuleException("TICKET-INV-03", "The assignee must be an active member of the ticket's team.");
        }

        AssigneeId = assigneeId;
        Status = Status.Assigned;
        AppendEvent(TicketEventType.Assigned, actorUserId, now, field: nameof(AssigneeId), oldValue: null, newValue: assigneeId.ToString(), note: null);
    }

    /// <summary>TICKET-WF-02.</summary>
    public void Unassign(Guid actorUserId, DateTimeOffset now)
    {
        RequireStatus(Status.Assigned, "TICKET-WF-02", nameof(Unassign));

        var previous = AssigneeId;
        AssigneeId = null;
        Status = Status.Open;
        AppendEvent(TicketEventType.Unassigned, actorUserId, now, field: nameof(AssigneeId), oldValue: previous?.ToString(), newValue: null, note: null);
    }

    /// <summary>TICKET-WF-03. The actor must be the assignee unless they are a Manager or Admin.</summary>
    public void StartWork(TicketActor actor, DateTimeOffset now)
    {
        RequireStatus(Status.Assigned, "TICKET-WF-03", nameof(StartWork));

        var isOverride = actor.Role is UserRole.Admin or UserRole.Manager;
        if (!isOverride && actor.UserId != AssigneeId)
        {
            throw new DomainRuleException("TICKET-WF-03", "Only the assignee (or a Manager/Admin override) may start work on this ticket.");
        }

        Status = Status.InProgress;
        AppendEvent(TicketEventType.StatusChanged, actor.UserId, now, field: nameof(Status), oldValue: nameof(Status.Assigned), newValue: nameof(Status.InProgress), note: null);
    }

    /// <summary>TICKET-WF-04 / TICKET-INV-05. Starts the SLA pause; PendingSince is recorded
    /// here, but SlaPausedMinutes is not touched until Resume or Resolve (SLA-RULE-07).</summary>
    public void PutOnHold(string reason, Guid actorUserId, DateTimeOffset now)
    {
        if (Status is not (Status.Assigned or Status.InProgress))
        {
            throw new DomainRuleException("TICKET-WF-04", $"Cannot put a ticket on hold from status {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainRuleException("TICKET-INV-05", "A pending reason is required.");
        }

        _statusBeforePending = Status;
        Status = Status.Pending;
        PendingReason = reason;
        PendingSince = now;
        AppendEvent(TicketEventType.PutOnHold, actorUserId, now, field: null, oldValue: null, newValue: null, note: reason);
    }

    /// <summary>TICKET-WF-05 / TICKET-WF-06 / SLA-RULE-07. Returns to whichever status the
    /// ticket held before going on hold.</summary>
    public void Resume(Guid actorUserId, DateTimeOffset now)
    {
        RequireStatus(Status.Pending, "TICKET-WF-05", nameof(Resume));

        AccruePendingPause(now);
        Status = _statusBeforePending ?? Status.Assigned;
        _statusBeforePending = null;
        PendingReason = null;
        AppendEvent(TicketEventType.Resumed, actorUserId, now, field: null, oldValue: null, newValue: null, note: null);
    }

    /// <summary>TICKET-WF-07 / TICKET-INV-06 / SLA-RULE-08.</summary>
    public void Resolve(Resolution resolutionCode, string resolutionNotes, Guid actorUserId, DateTimeOffset now)
    {
        if (Status is not (Status.InProgress or Status.Pending))
        {
            throw new DomainRuleException("TICKET-WF-07", $"Cannot resolve a ticket from status {Status}.");
        }

        if (resolutionNotes is null || resolutionNotes.Trim().Length < MinResolutionNotesLength)
        {
            throw new DomainRuleException("TICKET-INV-06", $"Resolution notes must be at least {MinResolutionNotesLength} characters.");
        }

        if (Status == Status.Pending)
        {
            AccruePendingPause(now);
        }

        ResolutionCode = resolutionCode;
        ResolutionNotes = resolutionNotes;
        ResolvedAt = now;
        SlaMet = now <= SlaDueAt;
        Status = Status.Resolved;
        _statusBeforePending = null;
        PendingReason = null;
        PendingSince = null;
        AppendEvent(TicketEventType.Resolved, actorUserId, now, field: null, oldValue: null, newValue: null, note: null);
    }

    /// <summary>TICKET-WF-08. Closer must be Manager/Admin, or the requester confirming.</summary>
    public void Close(TicketActor actor, DateTimeOffset now)
    {
        RequireStatus(Status.Resolved, "TICKET-WF-08", nameof(Close));

        var isPrivileged = actor.Role is UserRole.Admin or UserRole.Manager;
        var isRequester = actor.UserId == RequesterId;
        if (!isPrivileged && !isRequester)
        {
            throw new DomainRuleException("TICKET-WF-08", "Only a Manager/Admin or the requester may close this ticket.");
        }

        ClosedAt = now;
        Status = Status.Closed;
        AppendEvent(TicketEventType.Closed, actor.UserId, now, field: null, oldValue: null, newValue: null, note: null);
    }

    /// <summary>TICKET-WF-09 / SLA-RULE-09. Reopens to Assigned if an assignee is still on
    /// record, otherwise Open — consistent with TICKET-INV-04.</summary>
    public void Reopen(string reason, Guid actorUserId, DateTimeOffset now, IEnumerable<SlaConfiguration> currentConfigurations)
    {
        if (Status is not (Status.Resolved or Status.Closed))
        {
            throw new DomainRuleException("TICKET-WF-09", $"Cannot reopen a ticket from status {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainRuleException("TICKET-WF-09", "A reopen reason is required.");
        }

        ReopenCount++;
        SlaStartedAt = now;
        SlaTargetMinutes = SlaPolicy.ResolveTargetMinutes(currentConfigurations, WorkType, Priority);
        SlaDueAt = SlaPolicy.CalculateDueDate(now, SlaTargetMinutes);
        SlaPausedMinutes = 0;
        SlaMet = null;
        ResolvedAt = null;
        ClosedAt = null;
        ResolutionCode = null;
        ResolutionNotes = null;
        Status = AssigneeId.HasValue ? Status.Assigned : Status.Open;
        AppendEvent(TicketEventType.Reopened, actorUserId, now, field: null, oldValue: null, newValue: null, note: reason);
    }

    /// <summary>TICKET-WF-11.</summary>
    public void Reassign(Guid newAssigneeId, bool newAssigneeIsActiveTeamMember, Guid actorUserId, DateTimeOffset now)
    {
        if (Status is not (Status.Assigned or Status.InProgress or Status.Pending))
        {
            throw new DomainRuleException("TICKET-WF-11", $"Cannot reassign a ticket from status {Status}.");
        }

        if (!newAssigneeIsActiveTeamMember)
        {
            throw new DomainRuleException("TICKET-INV-03", "The new assignee must be an active member of the ticket's team.");
        }

        var previous = AssigneeId;
        AssigneeId = newAssigneeId;
        AssignmentChangeCount++;

        if (Status == Status.InProgress && previous != newAssigneeId)
        {
            Status = Status.Assigned;
        }

        AppendEvent(TicketEventType.Reassigned, actorUserId, now, field: nameof(AssigneeId), oldValue: previous?.ToString(), newValue: newAssigneeId.ToString(), note: null);
    }

    /// <summary>TICKET-INV-08 / SLA-RULE-06. Also gates what the authorization capability matrix
    /// calls "Change priority" — see <see cref="TicketAccessPolicy.CanTransition"/>.</summary>
    public void ChangePriority(Priority newPriority, IEnumerable<SlaConfiguration> currentConfigurations, Guid actorUserId, DateTimeOffset now)
    {
        RequireNotTerminal("TICKET-INV-08", nameof(ChangePriority));

        var oldPriority = Priority;
        Priority = newPriority;
        SlaTargetMinutes = SlaPolicy.ResolveTargetMinutes(currentConfigurations, WorkType, newPriority);
        SlaDueAt = SlaStartedAt.AddMinutes(SlaTargetMinutes + SlaPausedMinutes);

        AppendEvent(TicketEventType.PriorityChanged, actorUserId, now, field: nameof(Priority), oldValue: oldPriority.ToString(), newValue: newPriority.ToString(), note: null);
    }

    /// <summary>TICKET-INV-02 / TICKET-INV-08.</summary>
    public void ChangeCategory(int newCategoryId, int newCategoryTeamId, Guid actorUserId, DateTimeOffset now)
    {
        RequireNotTerminal("TICKET-INV-08", nameof(ChangeCategory));

        if (newCategoryTeamId != TeamId)
        {
            throw new DomainRuleException("TICKET-INV-02", "The category must belong to the ticket's team.");
        }

        var oldCategoryId = CategoryId;
        CategoryId = newCategoryId;
        AppendEvent(TicketEventType.CategoryChanged, actorUserId, now, field: nameof(CategoryId), oldValue: oldCategoryId.ToString(), newValue: newCategoryId.ToString(), note: null);
    }

    /// <summary>
    /// TICKET-INV-08. Moves the ticket to a different team; rejected on Resolved/Closed tickets
    /// (project-owner decision: resolved/closed tickets are completed historical work, and a
    /// team change afterward would corrupt historical ownership and team performance reporting).
    /// Clears the assignee and reverts to Open when previously Assigned/InProgress, to keep
    /// TICKET-INV-04 valid (an assignee on the old team is not necessarily valid on the new one).
    /// </summary>
    public void ChangeTeam(int newTeamId, Guid actorUserId, DateTimeOffset now)
    {
        RequireNotTerminal("TICKET-INV-08", nameof(ChangeTeam));

        var oldTeamId = TeamId;
        TeamId = newTeamId;

        if (AssigneeId.HasValue)
        {
            AssigneeId = null;
            if (Status is Status.Assigned or Status.InProgress)
            {
                Status = Status.Open;
            }
        }

        AppendEvent(TicketEventType.TeamChanged, actorUserId, now, field: nameof(TeamId), oldValue: oldTeamId.ToString(), newValue: newTeamId.ToString(), note: null);
    }

    /// <summary>Not gated by any invariant in docs/domain-model.md — none applies to due-date changes.</summary>
    public void ChangeDueDate(DateTimeOffset? newDueDate, Guid actorUserId, DateTimeOffset now)
    {
        var oldDueDate = DueDate;
        DueDate = newDueDate;
        AppendEvent(TicketEventType.DueDateChanged, actorUserId, now, field: nameof(DueDate), oldValue: oldDueDate?.ToString("O"), newValue: newDueDate?.ToString("O"), note: null);
    }

    /// <summary>TICKET-ENT-05 / TICKET-INV-09.</summary>
    public TicketComment AddComment(Guid authorId, string body, bool isInternal, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new DomainRuleException("TICKET-ENT-05", "Comment body is required.");
        }

        var comment = new TicketComment(authorId, body, isInternal, now);
        _comments.Add(comment);
        AppendEvent(TicketEventType.CommentAdded, authorId, now, field: null, oldValue: null, newValue: null, note: null);
        return comment;
    }

    /// <summary>Called once, by Infrastructure, after the reference sequence value is known.
    /// Not a domain rule — see docs/architecture.md §5.</summary>
    public void SetReference(string reference)
    {
        Reference = reference;
    }

    /// <summary>
    /// Test-only seam (see AssemblyInfo.cs InternalsVisibleTo): in real use, EF Core assigns
    /// <see cref="Id"/> directly via the database identity column without needing this method.
    /// Domain.Tests needs it to construct tickets with distinct ids for the ATTN-RULE-04
    /// final-tiebreak test, since Phase 2 has no persistence layer to assign ids naturally.
    /// </summary>
    internal void SetId(int id)
    {
        Id = id;
    }

    private void AccruePendingPause(DateTimeOffset now)
    {
        if (PendingSince is null)
        {
            return;
        }

        var pausedMinutes = (int)Math.Round((now - PendingSince.Value).TotalMinutes);
        SlaPausedMinutes += pausedMinutes;
        SlaDueAt = SlaDueAt.AddMinutes(pausedMinutes);
        PendingSince = null;
    }

    private void RequireStatus(Status required, string ruleCode, string operationName)
    {
        if (Status != required)
        {
            throw new DomainRuleException(ruleCode, $"Cannot perform {operationName} from status {Status}; requires {required}.");
        }
    }

    private void RequireNotTerminal(string ruleCode, string operationName)
    {
        if (Status is Status.Resolved or Status.Closed)
        {
            throw new DomainRuleException(ruleCode, $"Cannot perform {operationName} while the ticket is {Status}.");
        }
    }

    private void AppendEvent(TicketEventType type, Guid actorUserId, DateTimeOffset occurredAt, string? field, string? oldValue, string? newValue, string? note)
    {
        _events.Add(new TicketEvent(type, actorUserId, occurredAt, field, oldValue, newValue, note));
        UpdatedAt = occurredAt;
    }

    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrEmpty(title) || title.Length < MinTitleLength || title.Length > MaxTitleLength)
        {
            throw new DomainRuleException("TICKET-INV-01", $"Title must be between {MinTitleLength} and {MaxTitleLength} characters.");
        }
    }

    private static void ValidateDescription(string description)
    {
        if (string.IsNullOrEmpty(description) || description.Length > MaxDescriptionLength)
        {
            throw new DomainRuleException("TICKET-INV-01", $"Description is required and must be at most {MaxDescriptionLength} characters.");
        }
    }
}

using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;

namespace FabricDataGenerator;

/// <summary>
/// Generates synthetic tickets by driving the real <see cref="Ticket"/> domain aggregate methods
/// (<see cref="Ticket.Create"/>, <see cref="Ticket.Assign"/>, <see cref="Ticket.StartWork"/>, ...) —
/// the same methods <c>TicketService</c> calls in the real application. Every status transition,
/// SLA calculation, and appended <see cref="TicketEvent"/> is therefore produced by the actual
/// domain rules, not hand-rolled — a generated ticket can never violate a domain invariant because
/// the invariant-enforcing code is what created it.
/// </summary>
/// <remarks>
/// A single <see cref="Random"/>, seeded once by the caller, drives every decision in sequence, so
/// a fixed seed reproduces the same sequence of tickets deterministically (CLAUDE.md-style
/// reproducibility, per the brief's SEED requirement).
/// </remarks>
internal sealed class TicketGenerator
{
    private readonly TicketWorkContext _ctx;
    private readonly Random _random;
    private readonly double[] _dayWeights;
    private readonly double _totalDayWeight;
    private readonly int _totalDays;

    // Team pick weights, index-aligned with GenerationOptions/ReferenceDataBuilder's fixed team
    // order (Service Desk, Application Support, Infrastructure, Business Operations, Development)
    // — deliberately uneven so some teams carry more load than others.
    private static readonly double[] TeamWeights = [0.30, 0.25, 0.20, 0.15, 0.10];

    public TicketGenerator(TicketWorkContext ctx, Random random)
    {
        _ctx = ctx;
        _random = random;
        _totalDays = GenerationOptions.HistoryEnd.DayNumber - GenerationOptions.HistoryStart.DayNumber;
        _dayWeights = BuildDayWeights(_totalDays, random);
        _totalDayWeight = _dayWeights.Sum();
    }

    /// <summary>Builds the day-by-day sampling weight curve once: a slow growth trend, a strong
    /// weekday/weekend split, and occasional multi-day "incident storm" spikes.</summary>
    private static double[] BuildDayWeights(int totalDays, Random random)
    {
        var weights = new double[totalDays];
        for (var d = 0; d < totalDays; d++)
        {
            var date = GenerationOptions.HistoryStart.AddDays(d);
            var seasonal = 0.7 + 0.6 * (d / (double)Math.Max(1, totalDays - 1));
            var weekday = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.35 : 1.25;
            var spike = random.NextDouble() < 0.02 ? 2.5 + random.NextDouble() * 1.5 : 1.0;
            weights[d] = seasonal * weekday * spike;
        }

        return weights;
    }

    public IEnumerable<Ticket> Generate(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return GenerateOne();
        }
    }

    private Ticket GenerateOne()
    {
        var createdAt = PickCreationTimestamp();
        var teamIndex = WeightedIndex(TeamWeights);
        var team = _ctx.Teams[teamIndex];

        var categoryIndices = Enumerable.Range(0, _ctx.Categories.Count)
            .Where(idx => _ctx.CategoryTeamIndex[idx] == teamIndex)
            .ToList();
        var category = _ctx.Categories[categoryIndices[_random.Next(categoryIndices.Count)]];

        var workType = _random.NextDouble() < 0.8
            ? category.DefaultWorkType
            : RandomOtherWorkType(category.DefaultWorkType);

        var priority = PickPriority(workType);
        var project = _random.NextDouble() < 0.55 ? _ctx.Projects[_random.Next(_ctx.Projects.Count)] : null;
        var requester = _ctx.Users[_random.Next(_ctx.Users.Count)];

        var slaTargetMinutes = SlaPolicy.ResolveTargetMinutes(_ctx.SlaConfigurations, workType, priority);

        var title = BuildTitle();
        var description = Names.DescriptionTemplates[_random.Next(Names.DescriptionTemplates.Length)];

        DateTimeOffset? dueDate = _random.NextDouble() < 0.3
            ? createdAt.AddDays(2 + _random.Next(1, 14))
            : null;

        var ticket = Ticket.Create(
            title,
            description,
            workType,
            priority,
            requester.Id,
            team.Id,
            category.Id,
            team.Id,
            project?.Id,
            slaTargetMinutes,
            createdAt,
            plannedStartDate: null,
            dueDate: dueDate);

        SimulateLifecycle(ticket, team.Id, priority, workType, createdAt);

        return ticket;
    }

    private void SimulateLifecycle(Ticket ticket, int teamId, Priority priority, WorkType workType, DateTimeOffset createdAt)
    {
        var workers = _ctx.TeamWorkers[teamId];
        var lead = _ctx.TeamLead[teamId];
        var now = createdAt;
        var commentBudget = Poissonish(mean: 1.3, max: 6);

        MaybeComment(ticket, ref now, ref commentBudget, ticket.RequesterId, workers);

        if (_random.NextDouble() >= 0.85)
        {
            // Never assigned: stays Open. Some tickets are simply never picked up.
            return;
        }

        var assignee = WeightedPickWorker(workers);
        now = now.AddMinutes(AssignDelayMinutes(priority));
        ticket.Assign(assignee.Id, true, lead.Id, now);

        MaybeComment(ticket, ref now, ref commentBudget, ticket.RequesterId, workers);

        if (_random.NextDouble() >= 0.9)
        {
            // Assigned but never started: a "stuck in the queue" ticket.
            return;
        }

        now = now.AddMinutes(5 + _random.Next(0, 180));
        ticket.StartWork(new TicketActor(assignee.Id, assignee.Role), now);
        MaybeComment(ticket, ref now, ref commentBudget, ticket.RequesterId, workers);

        // Occasional reassignment(s) — increments AssignmentChangeCount.
        var currentAssignee = assignee;
        if (_random.NextDouble() < 0.12 && workers.Count > 1)
        {
            var next = WeightedPickWorker(workers, exclude: currentAssignee.Id);
            now = now.AddHours(1 + _random.Next(0, 12));
            ticket.Reassign(next.Id, true, lead.Id, now);
            currentAssignee = next;
            ResumeWorkIfNeeded(ticket, currentAssignee, ref now);
            MaybeComment(ticket, ref now, ref commentBudget, ticket.RequesterId, workers);

            if (_random.NextDouble() < 0.25 && workers.Count > 2)
            {
                var again = WeightedPickWorker(workers, exclude: currentAssignee.Id);
                now = now.AddHours(1 + _random.Next(0, 12));
                ticket.Reassign(again.Id, true, lead.Id, now);
                currentAssignee = again;
                ResumeWorkIfNeeded(ticket, currentAssignee, ref now);
            }
        }

        // Occasional mid-flight priority change.
        if (_random.NextDouble() < 0.15)
        {
            var newPriority = RandomOtherPriority(priority);
            now = now.AddHours(1 + _random.Next(0, 24));
            ticket.ChangePriority(newPriority, _ctx.SlaConfigurations, lead.Id, now);
            priority = newPriority;
        }

        // Occasional hold/resume cycle — some never resume within this run, staying Pending.
        if (_random.NextDouble() < 0.18)
        {
            now = now.AddHours(1 + _random.Next(0, 24));
            var reason = Names.HoldReasons[_random.Next(Names.HoldReasons.Length)];
            ticket.PutOnHold(reason, currentAssignee.Id, now);
            MaybeComment(ticket, ref now, ref commentBudget, ticket.RequesterId, workers);

            if (_random.NextDouble() >= 0.3)
            {
                now = now.AddHours(2 + _random.Next(0, 48));
                ticket.Resume(currentAssignee.Id, now);
            }
            else
            {
                // Stays Pending indefinitely within this dataset — a realistic "stalled" ticket.
                return;
            }
        }

        var ageDays = (GenerationOptions.HistoryEnd.ToDateTime(TimeOnly.MinValue) - createdAt.UtcDateTime).TotalDays;
        var resolveProbability = ageDays switch
        {
            > 60 => 0.97,
            > 21 => 0.85,
            > 7 => 0.60,
            _ => 0.25,
        };

        if (_random.NextDouble() >= resolveProbability)
        {
            // Still open/in progress at the end of the observation window.
            return;
        }

        now = now.AddMinutes(ResolutionDelayMinutes(priority, workType));
        var resolutionCode = PickResolutionCode(workType);
        var notes = Names.ResolutionNoteTemplates[_random.Next(Names.ResolutionNoteTemplates.Length)];
        ticket.Resolve(resolutionCode, notes, currentAssignee.Id, now);
        MaybeComment(ticket, ref now, ref commentBudget, ticket.RequesterId, workers);

        if (_random.NextDouble() < 0.7)
        {
            // Ticket.Close requires the actor to be a Manager/Admin or the ticket's own requester
            // (TICKET-WF-08); the assignee is neither in general, so closing is always done by the
            // team lead here.
            now = now.AddHours(12 + _random.Next(0, 108));
            ticket.Close(new TicketActor(lead.Id, lead.Role), now);
        }

        // Reopen (only legal from Resolved/Closed) — a bounded 6% chance, with a much smaller
        // chance of a second reopen, giving the dataset genuine reopen-history variety.
        if (_random.NextDouble() < 0.06)
        {
            now = now.AddHours(4 + _random.Next(0, 96));
            var reason = Names.ReopenReasons[_random.Next(Names.ReopenReasons.Length)];
            ticket.Reopen(reason, lead.Id, now, _ctx.SlaConfigurations);
            MaybeComment(ticket, ref now, ref commentBudget, ticket.RequesterId, workers);

            // A shorter secondary cycle: work resumes and (usually) resolves again.
            now = now.AddMinutes(30 + _random.Next(0, 240));
            ticket.StartWork(new TicketActor(currentAssignee.Id, currentAssignee.Role), now);

            if (_random.NextDouble() < 0.85)
            {
                now = now.AddMinutes(ResolutionDelayMinutes(priority, workType) / 2.0);
                var secondResolution = PickResolutionCode(workType);
                var secondNotes = Names.ResolutionNoteTemplates[_random.Next(Names.ResolutionNoteTemplates.Length)];
                ticket.Resolve(secondResolution, secondNotes, currentAssignee.Id, now);

                if (_random.NextDouble() < 0.6)
                {
                    now = now.AddHours(6 + _random.Next(0, 72));
                    ticket.Close(new TicketActor(lead.Id, lead.Role), now);
                }
            }
        }
    }

    /// <summary>
    /// <see cref="Ticket.Reassign"/> resets the ticket to <see cref="Status.Assigned"/> whenever the
    /// assignee actually changes while work was in progress (TICKET-WF-11) — so work must be
    /// explicitly restarted before any later <see cref="Ticket.Resolve"/> call, which requires
    /// <see cref="Status.InProgress"/> or <see cref="Status.Pending"/>. Without this, a reassigned
    /// ticket generated further down the lifecycle would throw a domain rule violation.
    /// </summary>
    private void ResumeWorkIfNeeded(Ticket ticket, GeneratedUser currentAssignee, ref DateTimeOffset now)
    {
        if (ticket.Status != Status.Assigned)
        {
            return;
        }

        now = now.AddMinutes(5 + _random.Next(0, 120));
        ticket.StartWork(new TicketActor(currentAssignee.Id, currentAssignee.Role), now);
    }

    private void MaybeComment(Ticket ticket, ref DateTimeOffset now, ref int budget, Guid requesterId, List<GeneratedUser> workers)
    {
        if (budget <= 0 || _random.NextDouble() >= 0.5)
        {
            return;
        }

        budget--;
        now = now.AddMinutes(5 + _random.Next(0, 240));

        var authorIsWorker = workers.Count > 0 && _random.NextDouble() < 0.75;
        var authorId = authorIsWorker ? workers[_random.Next(workers.Count)].Id : requesterId;
        var isInternal = authorIsWorker && _random.NextDouble() < 0.3;
        var body = Names.CommentTemplates[_random.Next(Names.CommentTemplates.Length)];

        ticket.AddComment(authorId, body, isInternal, now);
    }

    private DateTimeOffset PickCreationTimestamp()
    {
        var target = _random.NextDouble() * _totalDayWeight;
        var cumulative = 0.0;
        var dayIndex = _totalDays - 1;
        for (var d = 0; d < _totalDays; d++)
        {
            cumulative += _dayWeights[d];
            if (cumulative >= target)
            {
                dayIndex = d;
                break;
            }
        }

        var date = GenerationOptions.HistoryStart.AddDays(dayIndex);

        // 75% business hours (8-18), 25% any hour — nights/weekends still generate some tickets.
        var minuteOfDay = _random.NextDouble() < 0.75
            ? _random.Next(8 * 60, 18 * 60)
            : _random.Next(0, 24 * 60);

        return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMinutes(minuteOfDay);
    }

    private int WeightedIndex(double[] weights)
    {
        var total = weights.Sum();
        var target = _random.NextDouble() * total;
        var cumulative = 0.0;
        for (var i = 0; i < weights.Length; i++)
        {
            cumulative += weights[i];
            if (cumulative >= target)
            {
                return i;
            }
        }

        return weights.Length - 1;
    }

    private GeneratedUser WeightedPickWorker(List<GeneratedUser> workers, Guid? exclude = null)
    {
        var candidates = exclude is null ? workers : workers.Where(w => w.Id != exclude).ToList();
        if (candidates.Count == 0)
        {
            candidates = workers;
        }

        var total = candidates.Sum(w => w.WorkloadWeight);
        var target = _random.NextDouble() * total;
        var cumulative = 0.0;
        foreach (var candidate in candidates)
        {
            cumulative += candidate.WorkloadWeight;
            if (cumulative >= target)
            {
                return candidate;
            }
        }

        return candidates[^1];
    }

    private WorkType RandomOtherWorkType(WorkType current)
    {
        WorkType next;
        do
        {
            next = (WorkType)_random.Next(0, 4);
        }
        while (next == current);

        return next;
    }

    private Priority RandomOtherPriority(Priority current)
    {
        Priority next;
        do
        {
            next = (Priority)_random.Next(0, 4);
        }
        while (next == current);

        return next;
    }

    /// <summary>Incidents/Problems skew toward higher priority; Service Requests/Tasks skew low.</summary>
    private Priority PickPriority(WorkType workType)
    {
        double[] weights = workType switch
        {
            WorkType.Incident => [0.10, 0.30, 0.35, 0.25],
            WorkType.Problem => [0.15, 0.35, 0.35, 0.15],
            WorkType.ServiceRequest => [0.45, 0.40, 0.12, 0.03],
            WorkType.Task => [0.50, 0.35, 0.12, 0.03],
            _ => [0.25, 0.25, 0.25, 0.25],
        };

        return (Priority)WeightedIndex(weights);
    }

    private Resolution PickResolutionCode(WorkType workType)
    {
        (Resolution Code, double Weight)[] options = workType switch
        {
            WorkType.Incident =>
            [
                (Resolution.Fixed, 0.55), (Resolution.WorkaroundProvided, 0.25), (Resolution.NoFaultFound, 0.15), (Resolution.Duplicate, 0.05),
            ],
            WorkType.Problem =>
            [
                (Resolution.Fixed, 0.5), (Resolution.WorkaroundProvided, 0.2), (Resolution.Duplicate, 0.15), (Resolution.NoFaultFound, 0.15),
            ],
            _ =>
            [
                (Resolution.Completed, 0.75), (Resolution.Withdrawn, 0.15), (Resolution.Duplicate, 0.10),
            ],
        };

        var total = options.Sum(o => o.Weight);
        var target = _random.NextDouble() * total;
        var cumulative = 0.0;
        foreach (var (code, weight) in options)
        {
            cumulative += weight;
            if (cumulative >= target)
            {
                return code;
            }
        }

        return options[^1].Code;
    }

    /// <summary>Shorter for higher priority; also used to size the resolution-delay distribution
    /// below (both keyed off the same urgency signal).</summary>
    private double AssignDelayMinutes(Priority priority) => priority switch
    {
        Priority.Critical => 2 + _random.Next(0, 20),
        Priority.High => 5 + _random.Next(0, 60),
        Priority.Medium => 15 + _random.Next(0, 240),
        _ => 30 + _random.Next(0, 720),
    };

    /// <summary>
    /// Draws a resolution delay whose mean is deliberately set relative to the ticket's own SLA
    /// target so that, in aggregate, Critical/High tickets breach SLA noticeably more often than
    /// Low/Medium ones — without ever making the outcome deterministic for an individual ticket.
    /// </summary>
    private double ResolutionDelayMinutes(Priority priority, WorkType workType)
    {
        var target = SlaPolicy.ResolveTargetMinutes(_ctx.SlaConfigurations, workType, priority);

        // meanFactor > 1.0 means the average ticket of this priority tends to breach.
        var meanFactor = priority switch
        {
            Priority.Critical => 1.35,
            Priority.High => 1.10,
            Priority.Medium => 0.70,
            _ => 0.45,
        };

        // Category-driven variation: some work simply takes longer regardless of priority
        // (captured here as extra multiplicative noise rather than a per-category table, since
        // the category's own default work type already drives most of the real-world variance).
        var noise = 0.4 + _random.NextDouble() * 1.6; // 0.4x - 2.0x, mean ~1.2x

        return Math.Max(5, target * meanFactor * noise);
    }

    private int Poissonish(double mean, int max)
    {
        // Cheap approximation (not a true Poisson draw) that is good enough for a believable,
        // right-skewed comment-count distribution without pulling in a stats library. Uses the
        // generator's own seeded Random so the whole run stays deterministic.
        var value = 0;
        var p = 1.0;
        var l = Math.Exp(-mean);
        do
        {
            value++;
            p *= _random.NextDouble();
        }
        while (p > l && value < max);

        return Math.Min(value - 1, max);
    }

    private string BuildTitle()
    {
        var template = Names.TicketTitleTemplates[_random.Next(Names.TicketTitleTemplates.Length)];
        if (!template.Contains("{0}", StringComparison.Ordinal))
        {
            return template;
        }

        var filler = Names.TitleFillers[_random.Next(Names.TitleFillers.Length)];
        return string.Format(template, filler);
    }
}

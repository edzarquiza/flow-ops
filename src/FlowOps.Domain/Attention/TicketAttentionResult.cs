using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Attention;

/// <summary>One ticket paired with the signals <see cref="AttentionPolicy.Evaluate"/> found for it.</summary>
public sealed record TicketAttentionResult(Ticket Ticket, IReadOnlyList<AttentionSignal> Signals);

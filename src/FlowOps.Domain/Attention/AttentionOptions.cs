using FlowOps.Domain.Tickets;

namespace FlowOps.Domain.Attention;

/// <summary>
/// Rule ATTN-RULE-03: all signal thresholds live here, bound from configuration by
/// Infrastructure/Web — none are scattered constants inside <see cref="AttentionPolicy"/>.
/// Defaults match the seeded values in docs/domain-model.md's ATTN-RULE-02 signal table.
/// </summary>
public sealed class AttentionOptions
{
    public int UnassignedUrgentMinutes { get; init; } = 15;

    public IReadOnlyDictionary<Priority, int> AgingThresholdDays { get; init; } = new Dictionary<Priority, int>
    {
        [Priority.Critical] = 1,
        [Priority.High] = 3,
        [Priority.Medium] = 10,
        [Priority.Low] = 30,
    };

    public int StalledPendingDays { get; init; } = 3;

    public int StalledInProgressDays { get; init; } = 5;

    public int ChurnAssignmentChangeThreshold { get; init; } = 3;
}

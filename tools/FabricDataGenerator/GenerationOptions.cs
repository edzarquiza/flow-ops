namespace FabricDataGenerator;

/// <summary>
/// Target volumes and knobs for the synthetic Fabric demo dataset. Counts are approximate by
/// design (the brief allows "slightly different" numbers) — the actual generated counts are
/// whatever the probabilistic lifecycle simulation in <see cref="TicketGenerator"/> produces,
/// reported by <see cref="Validator"/> at the end of a run.
/// </summary>
public static class GenerationOptions
{
    /// <summary>Fixed seed so two runs produce the same logical dataset.</summary>
    public const int Seed = 20260928;

    public const int TeamCount = 5;
    public const int UserCount = 25;
    public const int CategoryCount = 12;
    public const int ProjectCount = 8;
    public const int TicketCount = 20_000;

    /// <summary>~21 months of operational history, inside the requested 18-24 month window.</summary>
    public static readonly DateOnly HistoryEnd = DateOnly.FromDateTime(DateTime.UtcNow.Date);
    public static readonly DateOnly HistoryStart = HistoryEnd.AddMonths(-21);

    public const string OutputDirectoryName = "fabric-demo-data";
}

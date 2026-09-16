namespace FlowOps.Web;

/// <summary>
/// FlowOps's native icon language — one family, one geometry system, inspired by (never traced
/// from) the approved <c>icons.png</c> art direction. Every icon shares a 24×24 viewBox, ~1.8px
/// stroke, round caps/joins, and minimal internal detail, so a sidebar glyph, a role badge glyph,
/// and a dashboard section glyph all read as the same product's handwriting. No icon font, no
/// third-party library (CLAUDE.md §11.1) — every shape is a plain inline SVG string, safe to emit
/// with <c>@Html.Raw</c> because none of it is user input. Icons used purely decoratively beside
/// text already carry the label — <c>aria-hidden="true"</c> everywhere, so nothing here adds
/// screen-reader noise.
/// </summary>
public static class Icons
{
    private const string A = "aria-hidden=\"true\" focusable=\"false\"";
    private const string S = "fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\"";

    // ---- The FlowOps brand mark: two crossing wave strokes — FLOW as the literal geometry,
    // never a gradient photograph of water. The front wave (bright teal) and back wave (dimmer
    // teal) cross once at the midpoint, reading as one continuous current rather than two
    // unrelated lines. Flat two-tone, no gradient — recognizable from 24px up to a full wordmark. ----

    /// <summary>The full-size mark, ~32×20 — sidebar/login size.</summary>
    public const string FlowMark = "<svg width=\"30\" height=\"19\" viewBox=\"0 0 32 20\" aria-hidden=\"true\" focusable=\"false\"><path d=\"M2 15c4-8 8-8 12-4s8 4 12-4\" fill=\"none\" stroke=\"#0F766E\" stroke-width=\"2.6\" stroke-linecap=\"round\" /><path d=\"M2 7c4 8 8 8 12 4s8-4 12 4\" fill=\"none\" stroke=\"#5FD3C4\" stroke-width=\"2.6\" stroke-linecap=\"round\" /></svg>";

    /// <summary>The same mark at a smaller, single-tone size — mobile top bar / tight spaces.</summary>
    public const string FlowMarkSmall = "<svg width=\"22\" height=\"14\" viewBox=\"0 0 32 20\" aria-hidden=\"true\" focusable=\"false\"><path d=\"M2 15c4-8 8-8 12-4s8 4 12-4\" fill=\"none\" stroke=\"#0F766E\" stroke-width=\"3\" stroke-linecap=\"round\" /><path d=\"M2 7c4 8 8 8 12 4s8-4 12 4\" fill=\"none\" stroke=\"#5FD3C4\" stroke-width=\"3\" stroke-linecap=\"round\" /></svg>";

    /// <summary>The same mark, larger — Login/Register's own brand block, where it stands alone
    /// above the wordmark rather than beside it.</summary>
    public const string FlowMarkLarge = "<svg width=\"46\" height=\"29\" viewBox=\"0 0 32 20\" aria-hidden=\"true\" focusable=\"false\"><path d=\"M2 15c4-8 8-8 12-4s8 4 12-4\" fill=\"none\" stroke=\"#0F766E\" stroke-width=\"2.6\" stroke-linecap=\"round\" /><path d=\"M2 7c4 8 8 8 12 4s8-4 12 4\" fill=\"none\" stroke=\"#5FD3C4\" stroke-width=\"2.6\" stroke-linecap=\"round\" /></svg>";

    /// <summary>The icon-only mark on its own tinted tile — app-icon/favicon scale, and anywhere
    /// the mark must read with no adjacent wordmark at all. The identical geometry is duplicated
    /// as a standalone file at wwwroot/favicon.svg (a browser favicon must be a real static asset,
    /// not an inline Razor string) — if this mark ever changes, update both.</summary>
    public const string FlowMarkTile = "<svg width=\"32\" height=\"32\" viewBox=\"0 0 32 32\" aria-hidden=\"true\" focusable=\"false\"><rect x=\"0.5\" y=\"0.5\" width=\"31\" height=\"31\" rx=\"7\" fill=\"#0D1719\" stroke=\"#243638\" /><path d=\"M6 20c3-6 6-6 9-3s6 3 9-3\" fill=\"none\" stroke=\"#0F766E\" stroke-width=\"2.4\" stroke-linecap=\"round\" /><path d=\"M6 12c3 6 6 6 9 3s6-3 9 3\" fill=\"none\" stroke=\"#5FD3C4\" stroke-width=\"2.4\" stroke-linecap=\"round\" /></svg>";

    // ---- Primary navigation ----

    public const string Dashboard = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><rect x=\"3.5\" y=\"3.5\" width=\"7\" height=\"7\" rx=\"1.6\" {S} /><rect x=\"13.5\" y=\"3.5\" width=\"7\" height=\"7\" rx=\"1.6\" {S} /><rect x=\"3.5\" y=\"13.5\" width=\"7\" height=\"7\" rx=\"1.6\" {S} /><rect x=\"13.5\" y=\"13.5\" width=\"7\" height=\"7\" rx=\"1.6\" {S} /></svg>";

    /// <summary>Stacked flow-lines — queued work moving through the same current the brand mark
    /// draws, rather than a generic bullet list.</summary>
    public const string WorkQueue = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><path d=\"M4 7c2.5-2 4.5-2 7 0s4.5 2 7 0\" {S} /><path d=\"M4 12.5c2.5-2 4.5-2 7 0s4.5 2 7 0\" {S} /><path d=\"M4 18c2.5-2 4.5-2 7 0\" {S} /></svg>";

    /// <summary>A break in the current — disruption in flow, not a generic bell.</summary>
    public const string AtRisk = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><path d=\"M3.5 12c2.5-2 4-2 6 0\" {S} /><circle cx=\"12\" cy=\"12\" r=\"1.7\" fill=\"currentColor\" /><path d=\"M14.5 12c2-2 3.5-2 6 0\" {S} /><path d=\"M12 5.5v2.2M12 16.3v2.2\" {S} /></svg>";

    /// <summary>Controlled intake into the flow — a plus inside the current's own circular gate.</summary>
    public const string Create = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8.2\" {S} /><path d=\"M12 8.3v7.4M8.3 12h7.4\" {S} /></svg>";

    public const string Members = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><circle cx=\"9\" cy=\"8.3\" r=\"3\" {S} /><circle cx=\"17\" cy=\"9.8\" r=\"2.3\" {S} /><path d=\"M3.6 19c0-3 2.4-5.3 5.4-5.3s5.4 2.3 5.4 5.3\" {S} /><path d=\"M15 19c0-2.2 1.3-4 3.4-4.7\" {S} /></svg>";

    public const string Settings = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"3.1\" {S} /><path d=\"M12 3.4v2.6M12 18v2.6M20.6 12H18M6 12H3.4M18 6l-1.8 1.8M7.8 16.2 6 18M18 18l-1.8-1.8M7.8 7.8 6 6\" {S} /></svg>";

    /// <summary>Three sliders at different settings — organization-wide controls, distinct from
    /// the personal-account gear (<see cref="Settings"/>).</summary>
    public const string Admin = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><path d=\"M4 6h6.5M14.5 6H20M4 12h1.5M9.5 12H20M4 18h11.5M19.5 18H20\" {S} /><circle cx=\"12.5\" cy=\"6\" r=\"1.8\" fill=\"currentColor\" /><circle cx=\"7\" cy=\"12\" r=\"1.8\" fill=\"currentColor\" /><circle cx=\"17.5\" cy=\"18\" r=\"1.8\" fill=\"currentColor\" /></svg>";

    /// <summary>Concentric rings — instance-wide scope, above any one organization. Distinct from
    /// <see cref="Admin"/>'s sliders (one organization's controls) and <see cref="Team"/>'s three
    /// units (one team) — this is the one glyph that means "every organization."</summary>
    public const string Platform = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8.2\" {S} /><circle cx=\"12\" cy=\"12\" r=\"4\" {S} /><circle cx=\"12\" cy=\"12\" r=\"1\" fill=\"currentColor\" /></svg>";

    public const string SignOut = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><path d=\"M9.5 4.5H5.8a1.3 1.3 0 0 0-1.3 1.3v12.4a1.3 1.3 0 0 0 1.3 1.3h3.7\" {S} /><path d=\"M10 12h10M16.5 7.5 20 12l-3.5 4.5\" {S} /></svg>";

    // ---- The hamburger stays a plain three-line glyph — a pure interaction primitive, not a
    // brand moment (Step 10 explicitly allows utility icons to be more conventional). ----
    public const string Menu = $"<svg width=\"22\" height=\"22\" viewBox=\"0 0 24 24\" {A}><path d=\"M4 7h16M4 12h16M4 17h16\" {S} /></svg>";

    // ---- Dashboard section icons — one per major concept, used strategically on panel heads. ----

    public const string Operations = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><path d=\"M2.5 12h4l2-6 4 12 2-9 1.5 3h5.5\" {S} /></svg>";

    public const string Attention = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"2.2\" fill=\"currentColor\" /><path d=\"M12 4v2.4M12 17.6V20M20 12h-2.4M6.4 12H4M17.3 6.7l-1.7 1.7M8.4 15.6l-1.7 1.7M17.3 17.3l-1.7-1.7M8.4 8.4 6.7 6.7\" {S} /></svg>";

    public const string Performance = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><path d=\"M5 19V13M11 19V8M17 19v-6.5M4 19h16\" {S} /></svg>";

    public const string Demand = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><path d=\"M3.5 16 9 9.5l4 4 7-8.5\" {S} /><path d=\"M15.5 5h4.5v4.5\" {S} /></svg>";

    /// <summary>Connected stages, left to right — the same "chain of steps" idea as the workflow
    /// rail, drawn small enough for a section head.</summary>
    public const string Pipeline = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><circle cx=\"4.5\" cy=\"12\" r=\"2.1\" {S} /><circle cx=\"12\" cy=\"12\" r=\"2.1\" {S} /><circle cx=\"19.5\" cy=\"12\" r=\"2.1\" fill=\"currentColor\" /><path d=\"M6.6 12h3.3M14.1 12h3.3\" {S} /></svg>";

    public const string Capacity = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><rect x=\"5\" y=\"14.5\" width=\"14\" height=\"4\" rx=\"1.2\" {S} /><rect x=\"5\" y=\"9.5\" width=\"14\" height=\"4\" rx=\"1.2\" {S} /><rect x=\"5\" y=\"4.5\" width=\"14\" height=\"4\" rx=\"1.2\" {S} /></svg>";

    /// <summary>A shield protecting the flow's own timing — SLA/Compliance.</summary>
    public const string Sla = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><path d=\"M12 3.4 18.5 6v5.2c0 4.4-2.9 7.4-6.5 8.4-3.6-1-6.5-4-6.5-8.4V6L12 3.4Z\" {S} /><path d=\"M8.8 12l2.2 2.2 4.2-4.8\" {S} /></svg>";

    /// <summary>A gauge with a fast, forward-leaning needle — optimized flow.</summary>
    public const string Efficiency = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><path d=\"M4 15.5a8 8 0 0 1 16 0\" {S} /><path d=\"M12 15.5 16 10\" {S} /><circle cx=\"12\" cy=\"15.5\" r=\"1.3\" fill=\"currentColor\" /></svg>";

    public const string Workload = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><circle cx=\"8.5\" cy=\"9\" r=\"2.6\" {S} /><circle cx=\"16\" cy=\"9\" r=\"2.6\" {S} /><path d=\"M3.6 19c0-2.8 2.2-5 4.9-5s4.9 2.2 4.9 5M12.6 14.3c.6-.2 1.3-.3 1.9-.3 2.7 0 4.9 2.2 4.9 5\" {S} /></svg>";

    /// <summary>Stacked structured records — Data/KPIs.</summary>
    public const string DataKpi = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><ellipse cx=\"12\" cy=\"6\" rx=\"7\" ry=\"2.6\" {S} /><path d=\"M5 6v6c0 1.4 3.1 2.6 7 2.6s7-1.2 7-2.6V6\" {S} /><path d=\"M5 12v6c0 1.4 3.1 2.6 7 2.6s7-1.2 7-2.6v-6\" {S} /></svg>";

    /// <summary>Three units meeting at one point — a team as a working group, distinct from the
    /// even 2x2 grid <see cref="Dashboard"/> already uses.</summary>
    public const string Team = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><rect x=\"3.5\" y=\"3.5\" width=\"7\" height=\"7\" rx=\"1.6\" {S} /><rect x=\"13.5\" y=\"3.5\" width=\"7\" height=\"7\" rx=\"1.6\" {S} /><rect x=\"8.5\" y=\"13.5\" width=\"7\" height=\"7\" rx=\"1.6\" {S} /></svg>";

    /// <summary>A folder — a project as a container of work, the Catalog module's Project icon.</summary>
    public const string Project = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><path d=\"M3.5 8V6.3A1.8 1.8 0 0 1 5.3 4.5h3.8l2 2.1h7.6a1.8 1.8 0 0 1 1.8 1.8V17a1.8 1.8 0 0 1-1.8 1.8H5.3A1.8 1.8 0 0 1 3.5 17V8Z\" {S} /></svg>";

    /// <summary>A shield with a small keyhole — protected authority, this session's Admin role icon.</summary>
    public const string RoleAdmin = $"<svg width=\"15\" height=\"15\" viewBox=\"0 0 24 24\" {A}><path d=\"M12 3 19 5.8V11c0 4.9-3 8.3-7 9.6-4-1.3-7-4.7-7-9.6V5.8L12 3Z\" {S} /><circle cx=\"12\" cy=\"10.6\" r=\"1.6\" {S} /><path d=\"M12 12.2v2.6\" {S} /></svg>";

    /// <summary>A headset arc — guided support/coordination, the Manager role icon.</summary>
    public const string RoleManager = $"<svg width=\"15\" height=\"15\" viewBox=\"0 0 24 24\" {A}><path d=\"M4.5 13.5v-2a7.5 7.5 0 0 1 15 0v2\" {S} /><rect x=\"3.2\" y=\"13\" width=\"3.2\" height=\"5\" rx=\"1.4\" {S} /><rect x=\"17.6\" y=\"13\" width=\"3.2\" height=\"5\" rx=\"1.4\" {S} /><path d=\"M19.5 18v.6a2.6 2.6 0 0 1-2.6 2.6H14.5\" {S} /></svg>";

    /// <summary>A person mark — direct operational support, the Agent role icon.</summary>
    public const string RoleAgent = $"<svg width=\"15\" height=\"15\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"8\" r=\"3.4\" {S} /><path d=\"M5 20c0-3.6 3.1-6.4 7-6.4s7 2.8 7 6.4\" {S} /></svg>";

    /// <summary>Bridged lenses — oversight across the whole picture, the Viewer role icon (a
    /// glasses silhouette, not a literal single eye).</summary>
    public const string RoleViewer = $"<svg width=\"15\" height=\"15\" viewBox=\"0 0 24 24\" {A}><circle cx=\"6.3\" cy=\"13\" r=\"3.3\" {S} /><circle cx=\"17.7\" cy=\"13\" r=\"3.3\" {S} /><path d=\"M9.6 12h4.8M3 11.5l-1.5-3M21 11.5l1.5-3\" {S} /></svg>";

    // ---- Status icons: a deliberate progression, not six unrelated glyphs — an open ring fills
    // clockwise as work advances, resolves to a check, and settles to a quiet closed ring. ----

    public const string StatusOpen = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8\" {S} /></svg>";

    public const string StatusAssigned = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8\" {S} /><path d=\"M12 4a8 8 0 0 1 0 16Z\" fill=\"currentColor\" /></svg>";

    public const string StatusInProgress = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8\" {S} /><path d=\"M12 4a8 8 0 0 1 6.9 12Z\" fill=\"currentColor\" /></svg>";

    public const string StatusPending = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8\" {S} /><circle cx=\"9\" cy=\"12\" r=\"1.15\" fill=\"currentColor\" /><circle cx=\"12\" cy=\"12\" r=\"1.15\" fill=\"currentColor\" /><circle cx=\"15\" cy=\"12\" r=\"1.15\" fill=\"currentColor\" /></svg>";

    public const string StatusResolved = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8\" {S} /><path d=\"M8.5 12.3l2.3 2.3 4.7-5.2\" {S} /></svg>";

    public const string StatusClosed = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8\" {S} opacity=\"0.55\" /></svg>";

    // ---- Priority icons: intensity as chevron count/direction, not color alone. ----

    public const string PriorityCritical = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><path d=\"M6 14l6-5 6 5M6 19l6-5 6 5\" {S} /></svg>";

    public const string PriorityHigh = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><path d=\"M6 15.5l6-6 6 6\" {S} /></svg>";

    public const string PriorityMedium = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><path d=\"M6 12h12\" {S} /></svg>";

    public const string PriorityLow = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><path d=\"M6 9.5l6 6 6-6\" {S} /></svg>";

    // ---- SLA icons ----

    public const string SlaMet = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8\" {S} /><path d=\"M8.3 12.3l2.4 2.4 5-5.6\" {S} /></svg>";

    public const string SlaWithin = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8\" {S} /><path d=\"M12 7.6V12l3 1.8\" {S} /></svg>";

    public const string SlaPaused = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8\" {S} /><path d=\"M10 8.6v6.8M14 8.6v6.8\" {S} /></svg>";

    public const string SlaAtRisk = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><path d=\"M12 4.2 20.5 19h-17L12 4.2Z\" {S} /><path d=\"M12 10v3.6\" {S} /><circle cx=\"12\" cy=\"16.2\" r=\"0.9\" fill=\"currentColor\" /></svg>";

    public const string SlaBreached = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8\" {S} /><path d=\"M9.3 9.3l5.4 5.4M14.7 9.3l-5.4 5.4\" {S} /></svg>";

    // ---- Utility icons — interaction primitives; more conventional by design (Step 10), but
    // built to the same stroke/geometry rules as everything above. ----

    public const string Search = $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" {A}><circle cx=\"10.5\" cy=\"10.5\" r=\"6.5\" {S} /><path d=\"M15.5 15.5 21 21\" {S} /></svg>";

    public const string Filter = $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" {A}><path d=\"M4 5h16l-6 7.5V19l-4 2v-8.5L4 5Z\" {S} /></svg>";

    public const string Calendar = $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" {A}><rect x=\"3.5\" y=\"5\" width=\"17\" height=\"15\" rx=\"2\" {S} /><path d=\"M3.5 9.5h17M8 3.2v3M16 3.2v3\" {S} /></svg>";

    public const string View = $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" {A}><path d=\"M2.5 12s3.5-6.5 9.5-6.5S21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z\" {S} /><circle cx=\"12\" cy=\"12\" r=\"2.4\" {S} /></svg>";

    public const string More = $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" {A}><circle cx=\"5.5\" cy=\"12\" r=\"1.3\" fill=\"currentColor\" /><circle cx=\"12\" cy=\"12\" r=\"1.3\" fill=\"currentColor\" /><circle cx=\"18.5\" cy=\"12\" r=\"1.3\" fill=\"currentColor\" /></svg>";

    public const string Chevron = $"<svg width=\"12\" height=\"12\" viewBox=\"0 0 24 24\" {A}><path d=\"M6 9.5l6 6 6-6\" {S} /></svg>";

    public const string Back = $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" {A}><path d=\"M15 5.5 8 12l7 6.5\" {S} /></svg>";

    public const string Forward = $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" {A}><path d=\"M9 5.5 16 12l-7 6.5\" {S} /></svg>";

    public const string ExternalLink = $"<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" {A}><path d=\"M9 6H5.5A1.5 1.5 0 0 0 4 7.5v11A1.5 1.5 0 0 0 5.5 20h11a1.5 1.5 0 0 0 1.5-1.5V15\" {S} /><path d=\"M13 4h7v7M20 4l-9.5 9.5\" {S} /></svg>";

    // ---- Metric icons: analytical/operational, deliberately distinct from the nav glyphs even
    // where the underlying concept overlaps (Step 11). ----

    public const string MetricOpenWork = $"<svg width=\"17\" height=\"17\" viewBox=\"0 0 24 24\" {A}><path d=\"M4 12h4l1.5 3h5L16 12h4\" {S} /><path d=\"M4 12v6a1.5 1.5 0 0 0 1.5 1.5h13A1.5 1.5 0 0 0 20 18v-6l-3-6H7l-3 6Z\" {S} /></svg>";

    public const string MetricOverdue = $"<svg width=\"17\" height=\"17\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"13\" r=\"7.2\" {S} /><path d=\"M12 9v4l2.6 1.6M9.5 3.5h5\" {S} /></svg>";
}

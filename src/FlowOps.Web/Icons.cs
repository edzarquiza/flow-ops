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

    // ---- The FlowOps brand mark: a ring with one wave crossing it — a single continuous current
    // held inside a boundary, replacing the earlier two-crossing-wave mark (brand-mark-integration
    // pass). CSS-driven, not hardcoded hex: colour comes from --fo-teal via the .brand-mark__ring/
    // .brand-mark__wave classes below, the same tokenisation rule every other themed element in
    // the app follows. One method, not a const per size — the 24×24 viewBox scales cleanly by
    // changing only the width/height attributes, never the path geometry. ----

    /// <summary>The brand mark at any size — sidebar (the default 24, matching every nav icon's
    /// own size), Login/Register's larger standalone block, or the mobile top bar's smaller one.
    /// Always paired with the visible "FlowOps" wordmark beside it, hence aria-hidden.</summary>
    public static string BrandMark(int size = 24) =>
        $"<svg class=\"brand-mark\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 24 24\" aria-hidden=\"true\" focusable=\"false\"><circle class=\"brand-mark__ring\" cx=\"12\" cy=\"12\" r=\"9.5\" fill=\"none\"></circle><path class=\"brand-mark__wave\" d=\"M4.5,12 Q8.25,5 12,12 T19.5,12\" fill=\"none\"></path></svg>";

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

    /// <summary>Three sliders at different settings — organization-wide controls.</summary>
    public const string Admin = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><path d=\"M4 6h6.5M14.5 6H20M4 12h1.5M9.5 12H20M4 18h11.5M19.5 18H20\" {S} /><circle cx=\"12.5\" cy=\"6\" r=\"1.8\" fill=\"currentColor\" /><circle cx=\"7\" cy=\"12\" r=\"1.8\" fill=\"currentColor\" /><circle cx=\"17.5\" cy=\"18\" r=\"1.8\" fill=\"currentColor\" /></svg>";

    /// <summary>Concentric rings — instance-wide scope, above any one organization. Distinct from
    /// <see cref="Admin"/>'s sliders (one organization's controls) — this is the one glyph that
    /// means "every organization."</summary>
    public const string Platform = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8.2\" {S} /><circle cx=\"12\" cy=\"12\" r=\"4\" {S} /><circle cx=\"12\" cy=\"12\" r=\"1\" fill=\"currentColor\" /></svg>";

    /// <summary>Phase 30D: a clock face with a hand — SLA target/deadline time, for the Platform
    /// sidebar's SLA Configuration link. Distinct from the smaller 14px SLA badge glyphs
    /// (<see cref="SlaWithin"/> and its siblings), which are sized for inline ticket-list use, not
    /// the ~20px sidebar nav convention every other link here follows.</summary>
    public const string Sla = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><circle cx=\"12\" cy=\"12\" r=\"8.2\" {S} /><path d=\"M12 7.6V12l3 1.8\" {S} /></svg>";

    /// <summary>An open book — the Guide's own nav icon. Two page-curves meeting at a spine, the
    /// same "shape carries the meaning, not a literal glyph" restraint every other nav icon here
    /// follows.</summary>
    public const string Guide = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><path d=\"M12 7c-1.9-1.4-4.2-2.1-6.8-2.1v12.6c2.6 0 4.9.7 6.8 2.1\" {S} /><path d=\"M12 7c1.9-1.4 4.2-2.1 6.8-2.1v12.6c-2.6 0-4.9.7-6.8 2.1\" {S} /><path d=\"M12 7v12.6\" {S} /></svg>";

    public const string SignOut = $"<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" {A}><path d=\"M9.5 4.5H5.8a1.3 1.3 0 0 0-1.3 1.3v12.4a1.3 1.3 0 0 0 1.3 1.3h3.7\" {S} /><path d=\"M10 12h10M16.5 7.5 20 12l-3.5 4.5\" {S} /></svg>";

    // ---- The hamburger stays a plain three-line glyph — a pure interaction primitive, not a
    // brand moment (Step 10 explicitly allows utility icons to be more conventional). ----
    public const string Menu = $"<svg width=\"22\" height=\"22\" viewBox=\"0 0 24 24\" {A}><path d=\"M4 7h16M4 12h16M4 17h16\" {S} /></svg>";

    // ---- Dashboard section icons — one per major concept, used strategically on panel heads. ----

    /// <summary>A gauge with a fast, forward-leaning needle — optimized flow.</summary>
    public const string Efficiency = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><path d=\"M4 15.5a8 8 0 0 1 16 0\" {S} /><path d=\"M12 15.5 16 10\" {S} /><circle cx=\"12\" cy=\"15.5\" r=\"1.3\" fill=\"currentColor\" /></svg>";

    /// <summary>Stacked structured records — Data/KPIs.</summary>
    public const string DataKpi = $"<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" {A}><ellipse cx=\"12\" cy=\"6\" rx=\"7\" ry=\"2.6\" {S} /><path d=\"M5 6v6c0 1.4 3.1 2.6 7 2.6s7-1.2 7-2.6V6\" {S} /><path d=\"M5 12v6c0 1.4 3.1 2.6 7 2.6s7-1.2 7-2.6v-6\" {S} /></svg>";

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

    public const string More = $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" {A}><circle cx=\"5.5\" cy=\"12\" r=\"1.3\" fill=\"currentColor\" /><circle cx=\"12\" cy=\"12\" r=\"1.3\" fill=\"currentColor\" /><circle cx=\"18.5\" cy=\"12\" r=\"1.3\" fill=\"currentColor\" /></svg>";

    public const string Forward = $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" {A}><path d=\"M9 5.5 16 12l-7 6.5\" {S} /></svg>";
}

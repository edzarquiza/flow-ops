namespace FlowOps.Web;

/// <summary>The four notice tones — the same semantic set (ok/info/warn/danger) used everywhere
/// else in the app, never a new palette.</summary>
public enum NoticeTone
{
    Success,
    Info,
    Warning,
    Danger,
}

/// <summary>
/// Phase 24A (UI/UX stabilization): presentation-only data for the shared <c>_Notice</c> partial —
/// "an action occurred / information needs attention," distinct from <c>.empty-state</c> ("there
/// is no data"), which several pages had misused to render success confirmations. Most callers
/// are an ordinary confirmation (<see cref="Urgent"/> <see langword="false"/>, announced via
/// <c>role="status"</c>); a caller sets <see cref="Urgent"/> only when the message demands
/// immediate attention (<c>role="alert"</c>) — never merely to make routine success feel more
/// dramatic.
/// </summary>
/// <param name="Message">The notice text — the only signal a screen reader gets, so it must stand
/// on its own without relying on the tone's colour.</param>
/// <param name="Tone">Which of the four semantic tones this notice carries.</param>
/// <param name="Urgent">True for <c>role="alert"</c> (interrupts, for something that needs
/// immediate attention); false (the default) for <c>role="status"</c> (polite, for an ordinary
/// confirmation).</param>
public sealed record NoticeViewModel(string Message, NoticeTone Tone = NoticeTone.Success, bool Urgent = false);

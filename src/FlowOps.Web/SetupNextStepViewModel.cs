namespace FlowOps.Web;

/// <summary>ADR-0026: presentation data for <c>_SetupNextStep.cshtml</c> — non-null only when the
/// hosting page's own setup step just became done and at least one other step is still
/// incomplete, so a caller never has to duplicate that condition in its own view.</summary>
public sealed record SetupNextStepViewModel(SetupSteps.Step NextStep);

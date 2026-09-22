namespace FlowOps.Application.Planning;

/// <summary>Raised when <see cref="FlowOps.Domain.Planning.PlanningAccessPolicy"/> denies a sprint
/// operation, or the project/sprint is not in the caller's own organization — "wrong organization"
/// and "does not exist" are deliberately indistinguishable (same shape as
/// <see cref="FlowOps.Application.Catalog.ProjectAccessDeniedException"/>).</summary>
public sealed class PlanningAccessDeniedException : Exception
{
    public PlanningAccessDeniedException(string message)
        : base(message)
    {
    }
}

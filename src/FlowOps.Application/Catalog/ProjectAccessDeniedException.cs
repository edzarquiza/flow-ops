namespace FlowOps.Application.Catalog;

/// <summary>
/// Raised when <see cref="FlowOps.Domain.Catalog.CatalogAccessPolicy"/> denies a project-management
/// operation, or when the target project does not belong to the caller's own organization — the same
/// non-disclosure shape <see cref="CategoryAccessDeniedException"/> uses for categories ("wrong
/// organization" and "does not exist" are deliberately indistinguishable).
/// </summary>
public sealed class ProjectAccessDeniedException : Exception
{
    public ProjectAccessDeniedException(string message)
        : base(message)
    {
    }
}

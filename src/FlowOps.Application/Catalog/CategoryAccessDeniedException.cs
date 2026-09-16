namespace FlowOps.Application.Catalog;

/// <summary>
/// Raised when <see cref="FlowOps.Domain.Catalog.CatalogAccessPolicy"/> denies a category-management
/// operation, or when the target team does not belong to the caller's own organization (the same
/// non-disclosure shape <see cref="FlowOps.Application.Tickets.TicketService"/>'s own
/// category/team/project organization checks use — "wrong organization" and "does not exist" are
/// deliberately indistinguishable).
/// </summary>
public sealed class CategoryAccessDeniedException : Exception
{
    public CategoryAccessDeniedException(string message)
        : base(message)
    {
    }
}

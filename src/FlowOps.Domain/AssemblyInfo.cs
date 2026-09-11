using System.Runtime.CompilerServices;

// Grants FlowOps.Domain.Tests access to internal members. Currently used only for
// Ticket.SetId — see the doc comment there for why this seam exists.
[assembly: InternalsVisibleTo("FlowOps.Domain.Tests")]

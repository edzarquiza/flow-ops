using System.Runtime.CompilerServices;

// Grants FlowOps.Application.Tests access to internal members. Currently used only for
// AttentionQueryService.BuildCandidateQuery, so ATTN-RULE-06's superset test can run the SQL
// prefilter in isolation and compare it against AttentionPolicy's own verdict. That test is
// required by the contract (CLAUDE.md §9.3), and the alternative — making the prefilter public —
// would widen the service's API purely for testing. Mirrors the same seam in FlowOps.Domain.
[assembly: InternalsVisibleTo("FlowOps.Application.Tests")]

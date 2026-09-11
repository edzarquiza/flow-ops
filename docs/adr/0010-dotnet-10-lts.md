# 0010. Target .NET 10 (LTS) instead of .NET 9

## Status
Accepted

## Context
CLAUDE.md originally specified .NET 9 as the build/runtime target and container base image
(§17 Docker). FlowOps is a new project starting development in September 2026. .NET 10, released
November 2025, is the current Long Term Support (LTS) release, supported through November 14,
2028. .NET 9 is a Standard Term Support (STS) release now in its maintenance period, reaching end
of support November 10, 2026. .NET 8, the previous LTS, is also in its maintenance period and
reaches end of support on that same date, November 10, 2026. A new project starting in September
2026 should not target a runtime that reaches end of support within about two months of the
project starting, when a current LTS with a multi-year remaining window is available.

## Decision
FlowOps targets **.NET 10** for all runtime projects, the Docker build stage
(`mcr.microsoft.com/dotnet/sdk:10.0`), and the Docker runtime stage
(`mcr.microsoft.com/dotnet/aspnet:10.0-alpine` or `-noble-chiseled`). CLAUDE.md §17 has been
updated accordingly. No other section of CLAUDE.md referenced a specific runtime version, so no
further changes were required.

## Alternatives considered
- **.NET 9 (STS):** Rejected — reaches end of support November 10, 2026, about two months after
  this project starts. Too close to end of support for a new project to target; it would need
  re-targeting almost immediately, before the project has meaningfully progressed.
- **.NET 8 (LTS):** Rejected — reaches end of support on the same date as .NET 9, November 10,
  2026, despite being the previous LTS. Choosing it over the current LTS for a project starting
  fresh in September 2026 would offer no longer a support window than .NET 9 while also being an
  older runtime, with no compensating benefit (no dependency in this project requires .NET 8
  specifically).
- **Do nothing (stay on .NET 9 as originally written):** Rejected for the reason above — the
  version was set before implementation began and does not reflect that .NET 10, the current LTS,
  is the appropriate target for a project starting at this point in the .NET release cycle.

## Consequences
- FlowOps runs on a runtime supported through November 14, 2028 — the longest remaining support
  window of the three releases considered, comfortably outliving the project's active development
  and portfolio-demonstration lifetime.
- The Docker images referenced in §17 must use the `10.0` tags; this ADR and the CLAUDE.md edit
  are the only artifacts affected at this stage, since no `.csproj`, `Dockerfile`, or CI
  configuration exists yet (Phase 1 of the project).
- Future phases (3, 13, 14) must set `<TargetFramework>net10.0</TargetFramework>` and reference
  the `10.0` image tags consistently; this ADR is the record to check if either drifts.
- No known trade-off: .NET 10 is a drop-in LTS successor with no removed feature this project
  depends on, and it avoids the near-term re-targeting that .NET 9 or .NET 8 would force by
  November 10, 2026.

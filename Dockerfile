# Phase 13 (CLAUDE.md §17): multi-stage, and nothing more clever than it needs to be.
# Build stage: the full SDK, needed only to restore/build/publish — never shipped in the final
# image. Runtime stage: the minimal ASP.NET Core Alpine image, non-root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files copied first, source copied after, so `dotnet restore` is cached across rebuilds
# that only change application code — the most common edit-rebuild cycle during development.
COPY FlowOps.sln .
COPY src/FlowOps.Domain/FlowOps.Domain.csproj src/FlowOps.Domain/
COPY src/FlowOps.Infrastructure/FlowOps.Infrastructure.csproj src/FlowOps.Infrastructure/
COPY src/FlowOps.Application/FlowOps.Application.csproj src/FlowOps.Application/
COPY src/FlowOps.Web/FlowOps.Web.csproj src/FlowOps.Web/
RUN dotnet restore src/FlowOps.Web/FlowOps.Web.csproj

# .dockerignore excludes tests/, docs/, bin/, obj/, and .git — only the four application projects'
# source reaches this layer.
COPY src/ src/

RUN dotnet publish src/FlowOps.Web/FlowOps.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

# CLAUDE.md §17: non-root user. $APP_UID is defined by Microsoft's own base image for exactly this
# purpose (consistent across the alpine/Debian/chiseled variants) — no manual UID/GID management,
# no extra packages.
USER $APP_UID

COPY --from=build /app/publish .

# .NET 8+ container base images default ASPNETCORE_HTTP_PORTS to 8080 already; EXPOSE only
# documents this for `docker compose`'s port mapping, it does not itself change the binding.
EXPOSE 8080

# CLAUDE.md §17: container HEALTHCHECK hitting /health. wget is provided by the Alpine base
# (BusyBox) itself, not installed for this purpose — no curl, no extra package, keeping the final
# image exactly as minimal as the base already is.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "FlowOps.Web.dll"]

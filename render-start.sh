#!/bin/sh
# Render Free tier has no Pre-Deploy Command (see docs/adr/0014-render-pre-deploy-database-initialization.md
# and docs/deployment.md). Render's Docker Command field replaces the image's own ENTRYPOINT/CMD
# entirely and does not reliably shell-interpret an inline quoted command, so this script exists
# as a real file in the image instead — set as the Docker Command explicitly for the Render
# service only, never as a Dockerfile ENTRYPOINT change (local `docker run`/Docker Compose keep
# using the existing plain `dotnet FlowOps.Web.dll` entrypoint unaffected).
#
# `set -e`: any non-zero exit below stops the script immediately.
set -e

# Migrate, then seed if FlowOps:Demo:Enabled — the same standalone command already used for
# Render's (unavailable, on this plan) Pre-Deploy Command. Runs to completion, or fails, before
# anything below executes.
dotnet FlowOps.Web.dll init-database

# Same "no Shell, no Pre-Deploy Command on this plan" constraint as above — this is the only
# reliably reachable place to run a one-time account-recovery command on Render's free tier.
# Deliberately opt-in and env-var-driven rather than ever hardcoding a password into this file:
# a plaintext password committed to git — even "temporarily" — stays in history forever on a
# public repo. Set RESET_PASSWORD_EMAIL and RESET_PASSWORD_VALUE in Render's Environment tab,
# deploy once, confirm the log line, then delete both variables so this never runs again.
if [ -n "$RESET_PASSWORD_EMAIL" ] && [ -n "$RESET_PASSWORD_VALUE" ]; then
    dotnet FlowOps.Web.dll reset-password "$RESET_PASSWORD_EMAIL" "$RESET_PASSWORD_VALUE" || true
fi

# `exec` replaces this shell process with the web server rather than running it as a child —
# without this, the web process would be a grandchild of the container's PID 1 (this script), and
# would not directly receive signals (e.g. SIGTERM on a Render restart/redeploy), risking an
# unclean shutdown. Only reached if `init-database` above exited 0.
exec dotnet FlowOps.Web.dll

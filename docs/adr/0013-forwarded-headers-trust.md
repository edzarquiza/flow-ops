# 0013. Forwarded-header trust for Render's reverse-proxy architecture

## Status
Accepted

## Context
Render terminates TLS at its own edge proxy and forwards plain HTTP to the application container;
the container's own Kestrel instance never sees a TLS handshake for a real client request. Without
telling ASP.NET Core to trust the proxy's `X-Forwarded-Proto`/`X-Forwarded-For` headers,
`HttpContext.Request.IsHttps` is `false` for every request once deployed — including every request
from a real HTTPS client — because Kestrel can only see the scheme of the hop that reached it
directly. CLAUDE.md §12's `CookieSecurePolicy.SameAsRequest` (both the Identity application cookie
and, as this phase discovered empirically, the antiforgery cookie) decides whether to mark a cookie
`Secure` based on exactly that flag. Left unconfigured, every authentication and antiforgery cookie
would ship without `Secure` in production, silently, despite every real client genuinely using
HTTPS.

## Decision
`Program.cs` registers `UseForwardedHeaders()` as the very first middleware in the pipeline —
before migrations, before Data Protection could touch the database, before HSTS, before the
security-headers middleware — configured to process `ForwardedHeaders.XForwardedFor |
ForwardedHeaders.XForwardedProto`, with `KnownIPNetworks` and `KnownProxies` both cleared.

Clearing those allow-lists is the specific trust decision this ADR exists to name. ASP.NET Core's
default behavior only trusts forwarded headers from loopback addresses — appropriate when the
proxy's address is fixed and known in advance, which it is not for a PaaS platform like Render: the
edge proxy's address is not published, may change, and is not a single fixed value this application
could enumerate. The actual trust boundary this relies on is **Render's network topology**, not the
header-processing configuration itself: a Render-hosted Docker web service is reachable from the
public internet *only* through Render's own edge proxy — the container has no separately exposed
public address an attacker could use to reach it directly and inject a forged `X-Forwarded-Proto`
header, bypassing the proxy entirely. Clearing the allow-list is safe **because** direct access to
the container is not possible in this hosting model, not because the header values themselves carry
any inherent trust. This is not "solved by calling `UseForwardedHeaders()`" — it is solved by that
call combined with Render's specific network isolation, which is why this ADR states the reasoning
explicitly rather than treating the API call as self-justifying.

The antiforgery cookie's `SecurePolicy` is configured separately from the Identity application
cookie — `services.Configure<AntiforgeryOptions>(...)`, alongside the existing
`ConfigureApplicationCookie` call — because ASP.NET Core does not default the antiforgery cookie's
`Cookie.SecurePolicy` to `SameAsRequest` on its own; this was verified directly (a forwarded-HTTPS
test request's antiforgery `Set-Cookie` carried no `Secure` attribute until this was added), not
assumed from the application cookie's configuration.

`UseHsts()` is registered immediately after `UseForwardedHeaders()` (Production only), so it reads
the already-corrected `Request.IsHttps` rather than Kestrel's own always-plain-HTTP view of the
connection.

## Alternatives considered
- **Configure specific `KnownProxies`/`KnownIPNetworks` entries**: rejected — Render does not
  publish a fixed, enumerable set of edge-proxy addresses for this to point at; the platform's own
  guidance for exactly this hosting shape is to clear the allow-list, relying on its network
  isolation instead.
- **`ForwardedHeaders.All` (including `X-Forwarded-Host`)**: rejected — nothing in this application
  currently generates absolute URLs from `Request.Host` in a way that a forwarded host header would
  affect; processing more than the two headers actually needed would be unjustified surface area.
- **Leaving `CookieSecurePolicy.Always` instead of `SameAsRequest`**: rejected again here for the
  same reason it was rejected when first set (Program.cs's own comment) — it would break local
  `dotnet run` and every `WebApplicationFactory` test, neither of which use TLS, and Render/Neon
  deployment does not change that local-development requirement.

## Consequences
- Both the Identity application cookie and the antiforgery cookie now correctly receive `Secure`
  once deployed behind Render's HTTPS-terminating proxy, verified directly against real HTTP
  responses (`Phase15DeploymentTests`), not inferred from configuration alone.
- `UseHsts()`, gated to Production, now also reads a corrected `Request.IsHttps` — without this
  ordering it would never add the header for a real deployed request either.
- If FlowOps is ever hosted somewhere other than Render — a platform where the container *is*
  directly reachable without passing through a single known proxy — clearing the allow-list would
  no longer be safe, and this decision would need to be revisited with that platform's actual
  network topology in mind, not copied forward unexamined.

# Public demo read-only edge

The `public-demo` security profile serves the real Task Server to anonymous
visitors. It is the W34 slice S4 deliverable from the
[public demo decision dossier](../demo-instanz/index.html) and implements its
launch requirement: same-origin TLS, an ephemeral viewer boundary, an explicit
endpoint allowlist, project-filtered SignalR, CSP and sandboxing, rate and body
limits, safe error surfaces, and disabled UI controls, with a typed denial for
every raw unsafe request.

## What this layer is and is not

The edge is the **second** barrier. The hard server execution lock (slice S2)
denies claims, starts, continuations, review, chat, previews, and post-steps
inside the server regardless of what reaches it. Removing the edge must never
make execution reachable, and the edge never grants execution. Its job is to
keep the reachable surface small, cheap to serve, and boring to probe.

The Angular read-only mode is explanatory UX only. A disabled button tells a
visitor why an action is unavailable; the server refuses the request either way.

## Profiles

| Profile | Intended use | Authentication | Mutations |
|---|---|---|---|
| `local` | One trusted operator on one machine | None; `X-Client-Id` is attribution | Allowed |
| `networked` | One organization on a hosted Task Server | Human sessions and Runner credentials | Allowed per role |
| `public-demo` | One disposable public demo VM | None; anonymous public read | Denied at the edge |

`public-demo` is a hardened profile. It inherits every restriction the
`networked` profile applies to development conveniences: no open CORS policy, no
`X-Client-Id` registration boundary, HSTS on, exception details off, and the
DevTools, diagnostics, filesystem-layer, and internal-probe routes are not
registered at all.

## Configuration

```jsonc
{
  "Security": { "Profile": "public-demo" },
  "PublicDemo": {
    "Projects": ["demo-app", "demo-platform"],  // ADR-0056 folder keys, ids, short codes, or display names
    "MaxRequestBodyBytes": 16384,
    "RequestsPerWindow": 240,
    "WindowSeconds": 60,
    "ViewerSessionMinutes": 120
  }
}
```

Every value is a startup-only ceiling. There is no management command, project
setting, or browser toggle that widens the visitor surface at runtime. A
contract that would widen it, such as an allowlist entry with an unsafe method
or an empty project list, fails the boot.

## Request decision

`PublicEdgePolicy.Decide` is pure and evaluated in this order. The order is part
of the contract: an unsafe method is refused before the allowlist is consulted,
so probing for route names through method errors reveals nothing.

| Step | Denial | Status |
|---|---|---|
| Health probes (`/healthz`, `/healthz/drain`) pass through | none | none |
| TLS required (trusts `X-Forwarded-Proto` from the local edge) | `public-demo-https-required` | 426 |
| Same-origin only; absent `Origin` counts as same-origin | `public-demo-origin-denied` | 403 |
| `/hubs/jobs` admits SignalR transport methods (`GET`, `POST` negotiate, `DELETE` long-poll close) and nothing else; the project filter lives in the hub | `public-demo-read-only` | 403 |
| Safe methods only (`GET`, `HEAD`, `OPTIONS`) | `public-demo-read-only` | 403 |
| Explicit endpoint allowlist, default deny | `public-demo-route-denied` | 403 |
| Body ceiling | `public-demo-body-too-large` | 413 |
| Project inside the seeded scene | `public-demo-project-denied` | 403 |
| Per-visitor request budget | `public-demo-rate-limited` | 429 |

A declared `Content-Length` over the ceiling is refused by the policy; the edge
also clamps `IHttpMaxRequestBodySizeFeature` to the same ceiling so a chunked
body, which declares nothing, cannot spend more than the contract allows.

Every denial body is `{ "error": "<code>", "message": "<one sentence>",
"profile": "public-demo" }`. It carries no route hint, upstream message, stack,
or failed path. Unhandled exceptions answer with a correlation id only.

## Endpoint allowlist

The visitor surface is the committed list in
[`backend/Features/PublicDemo/PublicEdgeAllowlist.cs`](../../../backend/Features/PublicDemo/PublicEdgeAllowlist.cs).
Everything else is denied by default, so a route added tomorrow is unreachable
from the public demo until a reviewed change puts it on the list.

The list covers product identity, projects and board, one card's metadata and
recorded run story, review evidence and artifacts, the Wiki, the Dossier
gallery, and cross-project search. Deliberately absent: authentication and
client registration, management, diagnostics, devtools, filesystem layer, CLI
and quota probes, prompts and admin config, Git history and repository file
reads, drift and analysis prompt builders, orchestrator and project chat,
supervisor, publish, and test runs.

`PublicEdgeInventoryGuardTests` resolves every entry against the live endpoint
table, so a renamed or removed route fails the build instead of silently
shrinking the demo to a wall of 403s. The same test writes
`public-demo-endpoint-inventory.json` beside the test assembly: the full
registered-route table classified allowed, allowed-sandboxed, or denied.

`GET /api/public-demo/edge` publishes the ceilings and the allowlist digest.
It is public on purpose, so an external probe can verify the deployed boundary
without an operator credential. The digest belongs in the release manifest as
`deploymentProfile: public-demo-readonly`.

## Ephemeral viewer session

The first allowed request mints `__Host-demo-viewer`: opaque random data,
HttpOnly, Secure, `SameSite=Strict`, path `/`, expiring after
`ViewerSessionMinutes`. It is public read authority, not a secret, and it
authorizes nothing. It exists so the request budget is per browser session
rather than per process. The limiter's identity table is capped, so a client
that discards its cookie on every request cannot turn the limiter itself into
the memory-exhaustion path.

## Project filter

One filter governs REST, search, and SignalR. `PublicDemoProjectScope` resolves
a handle written as an ADR-0056 folder key, project id, short code, display
name, or storage location and answers whether it is inside the seeded scene. A
task-addressed route resolves its project through the scanner; an unresolvable
task fails closed rather than counting as unscoped.

On the hub, a public visitor's connection joins only the seeded demo project
groups and never the unscoped group, and `SubscribeToConversation` refuses a job
outside the scene. Payload-bearing pushes are group-scoped in every hardened
profile.

## Transport and browser boundary

Every response carries `Content-Security-Policy` (`default-src 'self'`,
`object-src 'none'`, `frame-ancestors 'none'`, no remote origins),
`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Referrer-Policy: no-referrer`, `Cross-Origin-Opener-Policy: same-origin`,
`Cross-Origin-Resource-Policy: same-origin`, and a closed `Permissions-Policy`.
HSTS is on.

Routes that can carry seeded HTML (Wiki files and assets, task result
documents) are served under a stricter sandboxing policy: `default-src 'none'`
plus the CSP `sandbox` directive, so a document in the datastore cannot execute
script or reach a remote origin. Those responses use
`X-Frame-Options: SAMEORIGIN` and `frame-ancestors 'self'` so the app can still
frame them.

## Local verification

```bash
Security__Profile=public-demo bash scripts/worktree-test-stack.sh up --demo
eval "$(bash scripts/worktree-test-stack.sh env)"

# The stack serves plain HTTP, so present the proxy header the real edge sees.
curl -s -H 'X-Forwarded-Proto: https' "$BACKEND_URL/api/public-demo/edge"
curl -s -H 'X-Forwarded-Proto: https' -X POST "$BACKEND_URL/api/tasks"   # public-demo-read-only
curl -s -H 'X-Forwarded-Proto: https' "$BACKEND_URL/api/v1/management/status"  # public-demo-route-denied

bash scripts/worktree-test-stack.sh down
```

## Related

- [Public demo decision dossier](../demo-instanz/index.html): §1 recommendation,
  §6 security chapter, §8 slice plan.
- [Security overview](overview.md): the `local` and `networked` profiles.
- ADR-0056 in the [ADR archive](../../system/architecture/decisions/adr-archive.md)
  covers the demo datastore and the `DEMO-*` / `PLAT-*` namespace.

# Public read-only demo edge

The `public-demo` security profile serves a publicly browsable Agent Studio
instance that a visitor cannot change. It is slice S4 of the approved
[public demo dossier](../demo-instanz/index.html) (AGT-W34); §14 of that dossier
records what shipped and what is still open.

This page is the operator contract for the edge. It does not describe hosting.
The demo runs on its own disposable VM with no productive credential, data
volume, or route; that separation is slice S6 and needs a separate launch
approval.

## What the profile changes

Set `Security:Profile` to `public-demo`. The host then:

- inserts the public-demo edge ahead of authentication, routing, and every
  handler, so a denied request never reaches application code;
- enables HSTS, drops cross-origin allowances, and strips exception detail from
  error responses, exactly as the `networked` profile does;
- leaves the local-only diagnostic, dev-tool, internal-probe, and
  filesystem-layer routes unmapped;
- scopes SignalR fan-out to per-project groups and joins a visitor only to the
  announced demo projects;
- filters `/api/projects` to the announced demo projects and returns it with
  storage locations, repository paths, roots, remotes, watched URLs, and
  ownership mappings cleared. The route and the property names are unchanged, so
  no client has to know about the profile;
- filters `/api/search` results to the announced demo projects the same way,
  resolving each result's project through the registry first so a display-name
  or watch-path spelling cannot slip past the check.

There is no Studio account in this profile. Authority comes from the edge's
deny-by-default allowlist, not from a session.

## Settings

| Key | Default | Meaning |
|---|---|---|
| `Security:Profile` | `local` | Set to `public-demo` to serve the read-only edge. |
| `PublicDemo:Projects` | `demo-app`, `demo-platform` | Projects a visitor may observe. Matches the ADR-0056 demo datastore. |
| `PublicDemo:ViewerSessionMinutes` | `30` | Sliding lifetime of the ephemeral viewer boundary. |
| `PublicDemo:MaxViewerSessions` | `5000` | Ceiling on tracked viewers. The store sheds the least recently seen above it. |
| `PublicDemo:RequestsPerMinute` | `600` | Per-viewer rolling-minute request budget. Counts static assets too, so it must admit a cold shell load plus a browsing burst. Calibrate against the S6 load probe. |
| `PublicDemo:MaxRequestBodyBytes` | `16384` | Body ceiling. The read-only surface has no upload route. |

Every value is a ceiling. Raising one can only widen traffic inside the
read-only allowlist; none of them unlocks a mutation or an execution path.

## The endpoint allowlist

`backend/Features/PublicDemo/PublicDemoRoutes.cs` holds the inventory of
GET-reachable routes, grouped by the visitor story each entry serves. Anything
absent is unreachable, so a newly mapped route arrives denied until somebody
adds it deliberately.

Two tests guard it, both in `backend.Tests/PublicDemoEdgeEndpointTests.cs`:

- `EveryMappedRoute_IsEitherAllowlistedOrDeniedByDefault` walks every route the
  host maps and fails when a mutating verb is reachable;
- `EveryAllowlistEntry_MatchesARouteTheHostMaps` fails when an allowlist entry
  no longer corresponds to a mapped route, so the list cannot rot into a hole a
  future route walks into.

This is a reachability guard. It does not replace the server-side execution
denial matrix (slice S2), which the launch invariant still requires.

## Typed denials

Every rejected request answers with the same JSON envelope. `error` is the
stable machine code; the message stays generic so a probe learns nothing about
the route table.

```json
{
  "error": "public-demo-read-only",
  "message": "The public demo is read-only. This request was not executed.",
  "profile": "public-demo",
  "readOnly": true
}
```

| Code | Status | Cause |
|---|---|---|
| `public-demo-https-required` | 426 | Plain HTTP. Checked before everything else. |
| `public-demo-cross-origin-denied` | 403 | `Origin` does not match the demo's own scheme and host. Applies to the WebSocket upgrade too. |
| `public-demo-read-only` | 403 | Any POST, PUT, PATCH, or DELETE. Evaluated before the allowlist, so a forged or unknown path lands here too. |
| `public-demo-body-too-large` | 413 | Body over `MaxRequestBodyBytes`. |
| `public-demo-endpoint-denied` | 404 | A read outside the allowlist. |
| `public-demo-rate-limited` | 429 | The viewer's rolling-minute budget is spent. |

The one deliberate exception is the SignalR handshake: `POST /hubs/jobs/negotiate`
is admitted on that exact path and nowhere else.

## Browser boundary

Two content-security policies, chosen per response:

- **Application** - self-only for the Angular shell and the JSON API. No
  framing, no form posts, no base-tag rewrite, no remote embeds.
- **Seeded document** - `sandbox` with `default-src 'none'` for Wiki pages,
  Dossiers, evidence files, and screenshots. A script fragment that survived the
  scrub gate still has no origin authority, so it cannot read the demo's cookies
  or call the API.

Alongside them the edge sets `X-Content-Type-Options`, `X-Frame-Options`,
`Referrer-Policy`, `Cross-Origin-Opener-Policy`, `Cross-Origin-Resource-Policy`,
and a restrictive `Permissions-Policy`.

The viewer cookie (`asdemo_viewer`) is HttpOnly, Secure, SameSite=Strict, and
carries no `Expires`, so the boundary dies with the browser session. Treat it as
public, not secret: anyone can obtain one. It exists to give the request budget
and the hub scoping a stable subject, not to keep anybody out.

## What the visitor sees

The Angular shell reads the profile from `/api/auth/status`. In `public-demo` it
shows a banner explaining that the instance is a recorded snapshot, and every
control already wired to the shared `mutationsBlocked` gate disables itself:
create, drag-reorder, triage, and decision surfaces. A client-side interceptor
turns a stray write into a readable message instead of a round-trip.

That is explanatory UX. The server denies the mutation regardless of what the
browser shows.

## Local verification

`scripts/worktree-test-stack.sh up --demo` boots a real backend on isolated
ports against the seeded demo projects. Use it to look at the content. It is not
the public hosting topology and not the security boundary: it runs without TLS,
so the edge's transport rules cannot be exercised there. Prove those with the
test suite:

```bash
dotnet test backend.Tests/OrchestratorApi.Tests.csproj --filter "FullyQualifiedName~PublicDemo"
```

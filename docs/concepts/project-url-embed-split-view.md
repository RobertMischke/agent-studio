# Embedded Project URL preview with Orchestrator split view

Status: planning and design baseline, 2026-07-19. This document records the
target interaction and implementation seams. It does not authorize automatic
execution of a URL start rule and does not include the later browser-control
bridge.

Mockup: [project-url-embed-split-view.html](mockups/project-url-embed-split-view.html)

## Decision summary

- A Project URL opens as an embedded editor tab. Opening a browser tab is a
  secondary escape hatch only.
- The existing `OrchestratorSideSheetComponent` is the right pane. It already
  owns chat, Conversation View, open/close state, context selection, persisted
  width, and a resize handle. A second split shell or second chat instance
  would create competing state and is rejected.
- Desktop keeps the existing push layout: preview on the left, Orchestrator on
  the right. At compact widths the two surfaces switch instead of stacking.
- One configured URL maps to one stable Studio tab key. A compact selector in
  the preview chrome opens or focuses the other URLs for the same project.
- A host-side readiness check is the source of truth before the iframe mounts.
  It distinguishes offline, HTTP failure, and a known frame-policy refusal.
- Orchestrator access to page content is a separate follow-up slice. Start with
  a backend Playwright snapshot bridge, then add an optional `postMessage`
  adapter for precise in-page context. Defer CDP until a use case requires its
  operational cost.

## 1. Entry point and iframe lifecycle

### Primary entry

The primary click on a URL row or chip opens or focuses
`url-preview:<projectName>:<urlId>` in the main Studio editor area. This applies
to both existing discovery surfaces:

1. the configured URL child row in the Explorer workspace tree;
2. the URL row in Project Hub, including the Project Overview URL summary.

The row-level external-browser icon remains available but visually secondary.
The preview chrome also keeps an `Open externally` action for frame-policy
refusals and browser-specific failures.

The preview tab contains a compact toolbar with project and URL identity, the
configured address, readiness status, Reload, URL selector, external fallback,
and settings. The iframe fills all remaining editor space. It is not placed
inside Project Hub because the hub is a management surface and would waste
horizontal space needed by the preview.

### Frame sandbox

Use this fixed v1 baseline for cross-origin development URLs:

```html
<iframe
  sandbox="allow-scripts allow-same-origin allow-forms allow-modals allow-popups"
  referrerpolicy="no-referrer"
  title="Embedded preview of ...">
</iframe>
```

Rationale:

- `allow-scripts` is required by Angular, Vite, and similar development apps.
- `allow-same-origin` lets the framed app retain its own origin, storage, asset
  requests, and HMR behavior. A different localhost port is a different origin
  from Agent Studio, so the parent DOM remains protected by the browser's
  same-origin policy.
- `allow-forms` and `allow-modals` preserve ordinary application previews.
- `allow-popups` permits user-initiated links inside the preview, but popups
  remain sandboxed. Studio's own external fallback is still the canonical way
  to leave the embed.
- Do not grant top navigation, downloads, pointer lock, presentation, or
  storage-access escape in v1.

`allow-scripts` plus `allow-same-origin` is unsafe when the framed document is
the exact Agent Studio origin because such a document can remove its own
sandbox attribute. Reject that exact-origin configuration for embedding and
offer the external fallback. Loopback hosts on other ports are cross-origin and
remain the primary supported case. Only absolute HTTP and HTTPS URLs are
eligible. Mixed-content rules still apply if Studio is served over HTTPS and a
preview uses HTTP.

The sandbox limits browser capability, not trust in a start command. A
`StartRule` is an explicit operator-configured command and must run only after
the operator presses Start. Merely opening a preview never executes it.

### Readiness and frame-policy detection

A browser-side `fetch` cannot reliably inspect a cross-origin response. Agent
Studio therefore asks its backend to probe the registered URL and returns a
small readiness result:

```text
unknown | healthy | offline | timeout | http-error | frame-blocked
```

The probe performs a bounded GET, follows redirects, records the final HTTP
status, and evaluates the final response headers:

- `X-Frame-Options: DENY` is blocked.
- `X-Frame-Options: SAMEORIGIN` is blocked when the final target origin differs
  from the Studio origin.
- CSP `frame-ancestors` is evaluated against the actual Studio origin.
- A non-2xx response is an HTTP error, not a healthy preview.

The probe endpoint accepts only a registered project and URL ID. It must not
become a general URL-fetch endpoint. Bounded timeout, response-header-only
completion, redirect limits, cancellation, and structured logging are part of
the security contract. Before adding remote snapshot capability, apply an
explicit host/network allow policy to prevent SSRF.

Known limitation: response headers are the reliable early signal, but browser
extensions, a later in-frame navigation, or browser rendering failure can
still prevent a useful preview. The iframe `load` event does not prove that a
cross-origin page rendered correctly. A bounded load timeout may therefore
show `Embedding may be blocked`, never a false claim of certainty. The state
always offers Reload and `Open externally`.

### States

| State | Preview behavior | Primary action |
|---|---|---|
| Resolving / checking | Keep the iframe unmounted and show a spinner | Wait or Reload |
| Healthy | Mount iframe and show a loading overlay until its load signal | Reload |
| Offline / timeout | Explain that no server answered | Start preview when a StartRule exists |
| HTTP error | Show status code without embedding the error document | Reload or Restart |
| Frame blocked | Explain X-Frame-Options/CSP refusal | Open externally |
| Start failed | Show concise error plus command and CWD | Retry and inspect process output |
| URL removed | Keep the tab stable and link back to Project URLs | Open Project URLs |

Reload re-runs readiness. If healthy, it changes the trusted iframe source
identity so Angular navigates the frame without opening a browser tab.

## 2. Orchestrator split view

### Desktop

The existing application shell already hosts Studio and the Orchestrator side
sheet as flex siblings. Keep that ownership:

```text
Explorer | Project URL preview             | Orchestrator
         | iframe                          | Conversation View
         |                                 | Composer
```

Opening the side sheet claims its persisted width and pushes the preview. The
existing left-edge separator resizes the panel, clamped to a usable range.
Closing it gives the iframe the full editor width. The preview component owns
no Orchestrator state and does not duplicate the composer.

When a Project URL tab is active, navigation context selects the matching
project-scoped Orchestrator conversation. Pin behavior remains unchanged. The
context header may show the URL label and readiness as ambient context, but v1
does not claim that the Orchestrator can see the page body.

### Compact width

Below a compact breakpoint, or whenever the remaining preview would be less
than 520 px, do not stack two vertically short applications. Switch between
two full workspace modes:

- `Preview`: the iframe fills the available workspace.
- `Orchestrator`: the existing side sheet expands to the workspace width and
  the resize handle is disabled.

The same toolbar/status-bar toggle closes the Orchestrator and returns to the
still-selected preview tab. This is a responsive presentation of the same
side-sheet instance, not a new mobile component. Preserve the iframe component
where practical so switching does not unnecessarily restart app state.

## 3. Multiple URLs per project

Configured order remains the display order. For a project such as Coding Agent
Chat, the preview chrome shows a selector like:

```text
Lab :4201  |  Website :4202
```

Selecting another item opens or focuses its stable URL-preview tab. This keeps
standard Studio tab behavior, permits two previews to be open, and avoids a
special nested navigation stack. The Explorer URL rows and Project Hub list are
equivalent entry points. Readiness and owned process state remain keyed by
`projectId + urlId`, so starting Lab never changes Website's state.

## 4. StartRule to iframe flow

The flow is explicit and bounded:

```text
Open URL tab
  -> GET /api/projects/{id}/urls/{urlId}/readiness
  -> healthy: mount iframe
  -> offline: show Start preview
  -> operator presses Start
  -> POST /api/projects/{id}/urls/{urlId}/start
  -> show command output and poll readiness
  -> first healthy result: mount iframe
  -> timeout or process failure: keep error and retry controls in place
```

Do not replace readiness with a fixed sleep or with process existence. A server
started outside Studio can be healthy without an owned process, while a spawned
process can exist before its port is ready. Polling is canceled when the URL
changes or the tab is destroyed. Reload never silently starts a process.

## 5. Later slice: Orchestrator access to the embedded app

Cross-origin iframes are intentionally opaque to Agent Studio. The parent can
set `src`, observe coarse load events, and exchange explicit messages, but it
cannot read DOM, pixels, route state, console output, or dispatch DOM events.
Localhost does not weaken this boundary.

| Approach | Strength | Limitation | Decision |
|---|---|---|---|
| Cooperative `postMessage` protocol | Precise route, selected element, bounded DOM facts, and explicit actions in apps that install the adapter | Requires app instrumentation and strict origin, source-window, schema, version, and capability checks | Add after the snapshot baseline for owned apps |
| Backend Playwright snapshot bridge | Works across origins and restrictive page DOM; can return screenshot plus bounded accessibility/DOM summary | It opens a separate browser context, so cookies, transient state, and the exact iframe view can differ | Recommended first bridge, read-only first |
| CDP / DevTools bridge | Deep inspection, network, console, and interaction control | Requires a managed debug browser and target lifecycle; it cannot simply attach the backend to an arbitrary iframe in the user's browser | Defer |

The follow-up slice should expose a registered-URL-only snapshot endpoint,
rate-limit it, constrain reachable networks, redact secrets from logs, and
return an artifact ID plus bounded textual summary. The Orchestrator receives
the configured URL, readiness, capture time, screenshot artifact, and summary
as explicit context. It must say when the snapshot browser may differ from the
visible iframe.

For cooperative apps, define a small versioned protocol later:

```text
studio:hello -> preview:ready(capabilities)
studio:request-snapshot -> preview:snapshot(route, title, selectedElement?)
studio:highlight(selector) -> preview:result
```

Messages accept only the configured target origin and the current iframe's
`contentWindow`. Actions are capability-listed and operator-visible. Generic
script evaluation is out of scope.

## Implementation plan and file map

The repository already contains several of these seams. The plan below names
the intended ownership so the remaining work extends them instead of creating
parallel surfaces.

### Slice A: preview navigation and URL switching

- Keep the preview tab contract in
  `features/studio-shell/studio-shell.types.ts` and
  `services/studio-tab-state.service.ts`.
- Keep Explorer entry in
  `components/explorer-workspace-tree/explorer-workspace-tree.component.*`.
- Keep Project Hub entry in
  `features/project-detail/components/project-urls-panel/` and
  `components/project-hub-view/project-hub-view.component.ts`.
- Extend `features/project-detail/components/project-url-preview-tab/` with
  the same-project URL selector. Reuse the existing tab service to open or
  focus a selection.
- No Angular Router route is required. Studio editor tabs are the existing
  navigation model. Persist only stable IDs, never a `SafeResourceUrl`.

### Slice B: readiness, sandbox, and start lifecycle

- Keep display state in `ProjectUrlPreviewTabComponent` using signals and
  `OnPush`.
- Keep registry lookup in `ProjectUrlLookupService`, host probing in
  `ProjectUrlProbeService`, and owned-process state in
  `ProjectUrlProcessController`.
- Harden the backend policy seam in
  `backend/Features/Registry/ProjectUrlReadinessService.cs` and its registered
  URL endpoint in `RegistryEndpoints.cs`.
- Reuse `POST /api/projects/{id}/urls/{urlId}/start`, the process snapshot and
  stop endpoints, and `ProjectUrlProcessConsoleComponent`. Do not add a second
  process runner to the preview component.
- Add an exact-Studio-origin guard and evaluate framing policy against the
  final redirect URI.

### Slice C: split and responsive behavior

- Reuse
  `features/orchestrator/components/orchestrator-side-sheet/` and
  `OrchestratorPanelStateService` for the right pane and desktop resize.
- Keep the shell-level push layout in `app.html` and `app.scss`.
- Add compact exclusive-mode styling and accessibility behavior to that same
  side sheet. Disable the separator in exclusive mode and expose Preview and
  Orchestrator controls with `aria-pressed` or tab semantics.
- Feed active preview project and URL identity into the existing Orchestrator
  context selection. Do not add another chat component.

### Slice D: verification

- Component tests: sandbox policy, reload nonce, selector open/focus behavior,
  start polling cancellation, exact-origin rejection, and each state branch.
- Backend tests: status classification, final redirects, X-Frame-Options, CSP
  `frame-ancestors`, timeouts, and unknown project/URL rejection.
- Playwright: primary clicks stay in Studio, external fallback is secondary,
  two URLs switch independently, offline to start to healthy mounts the frame,
  blocked policy shows fallback, desktop resize, compact mode switch, both
  themes, and reduced motion.
- Persist representative desktop and compact screenshots under the task
  `results/` directory during implementation review.

### Slice E: page-access bridge

- Add a separate backend feature for registered-URL snapshots with Playwright,
  network policy, timeout, artifact retention, and audit events.
- Add a small frontend bridge service that contributes snapshot metadata to
  Orchestrator context without coupling it to iframe DOM access.
- Add the optional versioned `postMessage` adapter only for cooperative apps.
  Keep CDP behind a future architecture decision.

No ADR is needed for the v1 UI because it follows the existing Studio tab and
side-sheet contracts. The snapshot bridge should receive a security review and
an ADR if it introduces a managed browser lifecycle or mutation authority.

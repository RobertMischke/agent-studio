# Studio Route Restoration

**Status:** implemented baseline, 2026-07-24  
**Scope:** browser-addressable state in the Agent Software Studio shell  
**Visual companion:** [Route ownership diagram](studio-route-restoration-diagram.html)

## Context

The Studio previously restored its open editor tabs primarily from local
storage. Some surfaces had separate URL conventions: board filters used hash
key-value segments, task detail used top-level query parameters, and the legacy
Project Shell used a hash path. A Wiki page was addressable only while mounted
in the legacy shell. The same page inside a Studio Hub tab, and every Dossier,
depended on local tab state.

That split made the address bar an unreliable description of the visible
workspace. A reload could restore a different surface, and a copied URL could
not reproduce the operator's context.

## Decision

The URL names the active, shareable Studio surface. The canonical application
route is one path inside the hash:

```text
https://studio.example/#/<surface>/<stable-public-identifiers>?<route-local-state>
```

The convention is:

1. **Hash path for identity and hierarchy.** Surface, project slug, task key,
   Dossier id, Hub section, and settings section are path segments.
2. **One route-local hash query value for replaceable substate.** Wiki uses
   `page=` or `folder=`. Task detail uses one `view=<left-tab>:<right-tab>`
   value. One value avoids ambiguity with the shared hash segment delimiter.
3. **Sibling hash key-value segments for orthogonal state.** Board filters keep
   the established `filters=<encoded-expression>` segment and coexist with the
   route. Active expressions remain visible as removable chips above the board;
   the absence of `filters=` on a board route means the unfiltered view.
4. **No application route in the top-level query string.** The former
   `?task=`, `?job=`, and `?watchPath=` forms remain readable migration inputs.
   Successful resolution rewrites them to the canonical hash route and never
   republishes a filesystem path.
5. **Public identifiers only.** Routes contain project slugs, public task keys,
   and repository-defined Dossier ids. Internal `watchPath::id` keys stay out
   of browser history.

Primary surface changes use `history.pushState`, so Browser Back and Forward
move between Board, project, task, and settings surfaces. Replaceable substate
within one surface, such as a Hub rail, Wiki page, or detail tab, uses
`history.replaceState`. Cold boot canonicalization also replaces the current
entry. Existing task-opening flows may create a history entry first, then
replace that entry with the fully resolved canonical route. Browser
Back/Forward and `hashchange` both run the same route-in reconciliation.

## Complete route map

`<project>` is the lowercase public project slug. `<task>` is a stable task key
such as `AGT-2291`. Values are percent encoded.

| Surface | Canonical schema | Restored from the route | Deliberately transient |
|---|---|---|---|
| Workspace Board | `#/board` | Workspace-wide board and independent `filters=` segment | Lane scroll, card hover, open menus, focused lane, temporary loading state |
| Activity Feed | `#/feed` | Workspace-wide embedded Feed and its main-navigation state | Project and event filters, selected event, load-distribution period, scroll |
| Project Board | `#/projects/<project>/board` | Project scope and independent `filters=` segment | Lane collapse, scroll, drag state, selection marquee |
| Project Deck / Hub section | `#/projects/<project>[/<section>]` | Project Hub and active rail. Missing section means Overview | Rail scroll, expanded navigation groups, fetched panel cache, open dialog |
| Wiki Overview | `#/projects/<project>/wiki` | Project Hub, Wiki rail, Wiki landing | Tree expansion, scroll, search draft, hover state |
| Wiki page | `#/projects/<project>/wiki?page=<relative-path>` | Exact repository-relative Wiki document | Reading scroll, history flyout, editor draft, lightbox |
| Wiki folder | `#/projects/<project>/wiki?folder=<relative-path>` | Exact Wiki folder overview | Folder scroll, transient selection and hover |
| Workspace Dossiers | `#/workbenches` | Workspace-wide ordered overview | Inline decision expansion, decision draft, History disclosures, scroll, hover state |
| Project Dossiers | `#/projects/<project>/workbenches` | Project-scoped ordered overview | Inline decision expansion, decision draft, History disclosures, scroll, hover state |
| Dossier | `#/projects/<project>/workbenches/<workbench-id>` | Project, exact Dossier, repository HTML artifact | iframe scroll, in-artifact anchor, runtime script state |
| Task detail | `#/tasks/<task>` | Exact task and default Overview / Result tabs | Pane sizes, visible pane set, maximized pane, edit drafts, open menus, poll cache |
| Task detail with active tabs | `#/tasks/<task>?view=<detail-tab>:<inspector-tab>` | Exact task, left detail tab (`overview`, `timeline`, `evidence`, `code-review`, `description`) and right inspector tab (`protocol`, `activity`) | Activity subview, selected run, source viewer, splitter positions, composer draft |
| Epics overview | `#/epics` | Workspace-wide Epics overview | Expanded rows, sort hover, scroll |
| Project Epics | `#/projects/<project>/epics` | Project-scoped Epics overview | Expanded rows, sort hover, scroll |
| Epic detail | `#/epics/<task>` | Exact Epic task through the public task resolver | Expanded child task, pane layout, scroll |
| Workspace Settings | `#/workspace/settings[/<section>][/<detail>]` | Settings editor tab, active section, and the optional token-provider detail | Unsaved form values, confirmation dialogs, scroll |
| Project Settings | `#/projects/<project>/settings` | Project Hub and Settings rail | Unsaved form values, nested disclosure state, scroll |

Other editor tabs such as a commit diff, activity drilldown, and configured URL
preview remain implementation follow-ups. They use the same path rule when made
public; they must not introduce another top-level query or ad hoc hash grammar.

The project-scoped feed modal is retained as a quick-access compatibility
surface for project and status-bar entry points. It reuses the same feed
component and shared live store, but it does not own the canonical Feed URL.
The left Activity icon always opens the embedded `#/feed` tab.

## State ownership and precedence

Route-in has priority over local persistence:

1. Parse the route without mutating it.
2. Resolve public project slugs or task keys.
3. Open or focus the matching Studio tab.
4. Apply route-local substate after the surface exists.
5. Enable state-to-route mirroring only after hydration has completed.

This gate is required. Without it, a locally persisted active tab can replace a
shared Wiki or Dossier route during cold boot before project data arrives.

Local storage still owns workspace preferences and the open-tab collection. It
may seed the shell only when the URL does not identify another surface. URL
state always wins for the active surface.

The Orchestrator Chat side sheet is deliberately not another route owner.
Routes and tab changes update its next-message context but never open it. The
default-on **Open Chat from an empty project entry** preference applies only
when the operator explicitly selects a project while the editor has no open tab
context. After that entry, session storage preserves panel visibility and local
storage preserves its width. The project route continues to identify the Board,
Hub, task, Wiki, or Dossier surface behind the sheet. Pin, transcript scroll,
and composer draft remain transient browser state. This separation also
prevents persisted tab state from exposing another project's transcript during
cold boot.

## Shell tab target identity

Internal navigation opens or focuses an application tab. It does not replace
the current shell destination. The shared tab key is the target identity:

- Tasks use the canonical task key, regardless of whether the entry point was a
  board card, search result, task reference, feed item, or public route.
- Dossiers use project identity plus Dossier id.
- Wiki destinations use project identity plus target kind and exact repository
  path. The Wiki overview is its own target.
- Project Deck rails share one project target and adopt the newly requested
  rail. Pipeline row focus and similar rail state do not create another tab.

Task pane tabs, inspector tabs, Wiki viewer modes, and other replaceable
substate update the existing target. The shared `<app-pane-tabs>` control is
therefore not a shell tab strip and does not participate in shell close or MRU
behavior. External HTTP links retain normal browser behavior.

Each shell tab strip keeps a session-only activation history. Closing the
active tab with either its close button or a middle click returns to the most
recently active tab that is still open. If no history entry survives, the
previous last-tab fallback applies.

## Invalid and stale routes

- An unknown project slug is left intact while the project registry is
  loading. If it remains unresolved, no private path is inferred.
- An unknown Hub section falls back to the Hub Overview.
- An unknown Task detail tab falls back to `overview`; an unknown inspector tab
  falls back to `protocol`.
- An unknown Workspace Settings section is not treated as a settings route.
- A missing Wiki document is handled by the Wiki's existing not-found state.
- A missing Dossier is handled by the Dossier viewer's existing error
  state.
- Legacy task query routes are accepted once and canonicalized after the server
  resolves the public task.

## Verification contract

Every public surface needs three directions of coverage:

| Direction | Assertion |
|---|---|
| Route to state | A cold navigation opens the named surface and route-local tab/page |
| State to route | A user navigation updates the route with no stale surface state |
| Reload roundtrip | Reloading the generated URL restores the same visible surface |

Playwright route tests use mocked API responses for determinism and persist
review screenshots under the managed task's `results/` directory.

## Implementation slices

1. **Core:** Wiki page and Dossier routes, including cold reload and copied
   URLs.
2. **Task detail:** public task path plus both active tab strips.
3. **Remaining named surfaces:** Board, Hub sections, Epics, and Settings use
   the shared route parser/builder. Diff, activity drilldown, and URL preview
   remain explicit follow-ups.

## Living knowledge log

- **2026-07-24:** Established the canonical hash-path contract, route hydration
  precedence, Wiki and Dossier deep links, Task detail tab state, and the
  route/state/reload test matrix.
- **2026-07-30:** Promoted the workspace Activity Feed to the embedded
  `#/feed` main route. Kept the existing project modal as a quick-access
  compatibility surface rather than a second primary route.
- **2026-08-09:** Defined internal application-tab target identity, exact Wiki
  path deduplication, and session-only MRU return when the active tab closes.
- **2026-08-09:** Added workspace-wide and project-scoped Dossier overview
  routes while retaining the existing item route for the viewer.
- **2026-08-10:** Made board filter state visible and removable on the board and
  defined a board route without `filters=` as the unfiltered state.
- **2026-08-10:** Made the existing Orchestrator Chat side sheet the default
  project entry after route hydration, while keeping its visibility out of the
  canonical URL and preserving task deep links.
- **2026-08-11:** Restricted that default to explicit project entry from an
  empty editor. Navigation no longer opens the sheet; open state and width now
  retain the operator's posture while next-message context follows the route.

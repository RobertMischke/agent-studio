# Studio Route Restoration

**Status:** implemented baseline, 2026-07-24  
**Scope:** browser-addressable state in the Agent Software Studio shell  
**Visual companion:** [Route ownership diagram](studio-route-restoration-diagram.html)

## Context

The Studio previously restored its open editor tabs primarily from local
storage. Some surfaces had separate URL conventions: board filters used hash
key-value segments, task detail used top-level query parameters, and the legacy
Project Shell used a hash path. A Wiki page was addressable only while mounted
in the legacy shell. The same page inside a Studio Hub tab, and every Workbench,
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
   Workbench id, Hub section, and settings section are path segments.
2. **One route-local hash query value for replaceable substate.** Wiki uses
   `page=` or `folder=`. Task detail uses one `view=<left-tab>:<right-tab>`
   value. One value avoids ambiguity with the shared hash segment delimiter.
3. **Sibling hash key-value segments for orthogonal state.** Board filters keep
   the established `filters=<encoded-expression>` segment and coexist with the
   route.
4. **No application route in the top-level query string.** The former
   `?task=`, `?job=`, and `?watchPath=` forms remain readable migration inputs.
   Successful resolution rewrites them to the canonical hash route and never
   republishes a filesystem path.
5. **Public identifiers only.** Routes contain project slugs, public task keys,
   and repository-defined Workbench ids. Internal `watchPath::id` keys stay out
   of browser history.

Surface changes and tab changes use `history.replaceState`. They keep the
current history entry honest without adding an entry for every rail or detail
tab click. Existing task-opening flows may create a history entry first, then
replace that entry with the fully resolved canonical route. Browser
Back/Forward and `hashchange` both run the same route-in reconciliation.

## Complete route map

`<project>` is the lowercase public project slug. `<task>` is a stable task key
such as `AGT-2291`. Values are percent encoded.

| Surface | Canonical schema | Restored from the route | Deliberately transient |
|---|---|---|---|
| Workspace Board | `#/board` | Workspace-wide board and independent `filters=` segment | Lane scroll, card hover, open menus, focused lane, temporary loading state |
| Project Board | `#/projects/<project>/board` | Project scope and independent `filters=` segment | Lane collapse, scroll, drag state, selection marquee |
| Project Deck / Hub section | `#/projects/<project>[/<section>]` | Project Hub and active rail. Missing section means Overview | Rail scroll, expanded navigation groups, fetched panel cache, open dialog |
| Wiki Overview | `#/projects/<project>/wiki` | Project Hub, Wiki rail, Wiki landing | Tree expansion, scroll, search draft, hover state |
| Wiki page | `#/projects/<project>/wiki?page=<relative-path>` | Exact repository-relative Wiki document | Reading scroll, history flyout, editor draft, lightbox |
| Wiki folder | `#/projects/<project>/wiki?folder=<relative-path>` | Exact Wiki folder overview | Folder scroll, transient selection and hover |
| Workbench | `#/projects/<project>/workbenches/<workbench-id>` | Project, exact Workbench, repository HTML artifact | iframe scroll, in-artifact anchor, runtime script state |
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

## State ownership and precedence

Route-in has priority over local persistence:

1. Parse the route without mutating it.
2. Resolve public project slugs or task keys.
3. Open or focus the matching Studio tab.
4. Apply route-local substate after the surface exists.
5. Enable state-to-route mirroring only after hydration has completed.

This gate is required. Without it, a locally persisted active tab can replace a
shared Wiki or Workbench route during cold boot before project data arrives.

Local storage still owns workspace preferences and the open-tab collection. It
may seed the shell only when the URL does not identify another surface. URL
state always wins for the active surface.

## Invalid and stale routes

- An unknown project slug is left intact while the project registry is
  loading. If it remains unresolved, no private path is inferred.
- An unknown Hub section falls back to the Hub Overview.
- An unknown Task detail tab falls back to `overview`; an unknown inspector tab
  falls back to `protocol`.
- An unknown Workspace Settings section is not treated as a settings route.
- A missing Wiki document is handled by the Wiki's existing not-found state.
- A missing Workbench is handled by the Workbench viewer's existing error
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

1. **Core:** Wiki page and Workbench routes, including cold reload and copied
   URLs.
2. **Task detail:** public task path plus both active tab strips.
3. **Remaining named surfaces:** Board, Hub sections, Epics, and Settings use
   the shared route parser/builder. Diff, activity drilldown, and URL preview
   remain explicit follow-ups.

## Living knowledge log

- **2026-07-24:** Established the canonical hash-path contract, route hydration
  precedence, Wiki and Workbench deep links, Task detail tab state, and the
  route/state/reload test matrix.

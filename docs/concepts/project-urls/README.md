# Project URLs

## Problem

Projects tracked in the registry (`ProjectRecord`) have no place to record the
URLs someone actually watches while the project runs. For a project like
"Coding Agent Chat" that means a presentation website, a workbench host, and a
demo/playground app - today there is nowhere to put those, and no memory of
how to start each one when it's not running. The developer either keeps this
in their head or greps the target repo's `README.md`/`package.json` every
time.

Cross-project audit (`agent-taskboard-workspace/.metadata/projects.json`,
10 registered projects) confirms the shape needed:

| Project | URLs that matter | Where the start command lives |
|---|---|---|
| Coding Agent Chat (PROJ-014) | website (4202), conversation-lab (4201), workbench (5055, .NET) | top-level `package.json` scripts |
| Agent Studio Website (PROJ-012) | preview of the current mockup variant (4184) | **only in `README.md`**, not in a script |
| Coding Agent Runner (PROJ-011) | static `website/index.html` | none - "open the file", no process to start |
| Agent Studio Marketing (PROJ-013), Runbook, Playwright Test, Privat, New, Lotta Dashboard | none | n/a |

Takeaways that shaped the design below:
- **N URLs per project, including zero.** Most registered projects have no
  runnable URL at all; this must not be a mandatory field.
- **The start rule is not always a `package.json` script.** The website mockup
  repo's real run command is a fenced code block inside `README.md`. Any
  detection helper has to read both sources.
- **Not every URL needs a start rule.** A static HTML file has nothing to
  "start" - the row can exist with no command attached.

## Recommendation / scope

Add an optional, ordered list of URLs to `ProjectRecord`, each with an
optional start rule. Management of that list lives in the Project Hub
side-sheet as **its own rail entry** ("Project URLs", peer of
Overview/Drift/Observability in the Insight group) - not embedded inside the
Overview page, and not part of any task-level chat surface. Quick, day-to-day
*access* to each URL does **not** live inside Project Hub at all - see "UI -
two surfaces" below.

### Data model

```csharp
public record ProjectUrlRecord
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";        // "Presentation website"
    public string Url { get; init; } = "";
    public int SortOrder { get; init; }
    public ProjectUrlStartRule? StartRule { get; init; }
}

public record ProjectUrlStartRule
{
    public string Command { get; init; } = "";       // "npm run website"
    public string? Cwd { get; init; }                // defaults to RepositoryPath
    public int? Port { get; init; }
    public string Source { get; init; } = "manual";  // "manual" | "package-json" | "readme"
}
```

`ProjectRecord.Urls: IReadOnlyList<ProjectUrlRecord>` (default empty), plus
`AddUrl` / `UpdateUrl` / `RemoveUrl` / `ReorderUrls` mutators on
`ProjectRegistry`, following the existing `SetRepositoryPath` pattern exactly
(absolute-path-style validation where relevant, `MutateLocked`, persist to
`projects.json`). Wire DTOs mirror `RepositoryPath`'s nullable-value +
`Clear*` pattern used in `UpdateProjectRequest`.

### Detection service (new - nothing like it exists today)

Given a project's `RepositoryPath`, a new backend service:
1. Reads `package.json` `scripts` (and, for Angular workspaces, each
   sub-project's configured port in `angular.json`).
2. Scans `README.md` for fenced code blocks containing a run command
   (`npm run`, `npm start`, `ng serve`, `dotnet run`, `npx`) and a nearby port
   number, exactly like the `agent-studio-for-software-website` case in the
   table above.
3. Returns suggestions (label, command, cwd, port, source) that the UI offers
   as one-click "fill from suggestion" chips - never auto-applied.

### UI - two surfaces, not one

Revised after feedback: this is **not** a Project-Hub-internal tree with
subpages. It is two separate, existing surfaces, each getting exactly the
extension it needs:

**1. Explorer workspace tree** (`explorer-workspace-tree.component.ts/html` -
the persistent left sidebar in the screenshot, listing Board / Project Hub /
Wiki / Backlog / Epics per project today). Each configured URL becomes one
more `<app-tree-row level="child">` per project, appended after the existing
five, rendered from a `@for` loop over the project's `Urls` (today those five
rows are hand-written literals, not data-driven - see "Explorer tree" below).
New URL rows use a new `link`/globe glyph, a small status dot
(`running`/`offline`) instead of a count badge, and **clicking one opens the
URL directly** (new browser tab) - it does not open Project Hub at all. A
project with zero configured URLs shows zero extra rows (e.g. Agent Studio
Marketing in the screenshot).

**2. Project Hub side-sheet** gets exactly **one** new rail entry, **"Project
URLs"**, in the Insight group - a single flat page, no children, no
sub-navigation. This is where the actual *management* happens: the full list
with status pills, the build/restart strip, and "Add URL". No per-URL detail
subpage - editing happens inline or via a small dialog on this one page. The
quick "jump to this URL" access lives in the Explorer tree (surface 1), not
here; Project Hub is for configuring and rebuilding, not for daily navigation.

**Add URL** (on the Project Hub page) offers the detected suggestions (from
the service above) plus a manual fallback form; adding one immediately
inserts its row in the Explorer tree too.

New `link`/globe `StudioIconName` needed (none of the existing 40+ icons
cover this) - used in both surfaces.

See [`ui.html`](ui.html) for a clickable prototype of this (static, no real
process spawning - "Rebuild"/"Restart"/"Start" just simulate the
offline -> building -> running transition with a timeout). Open it directly in
a browser, or through the in-app Wiki once this folder syncs.

### Explorer tree today: hardcoded, not data-driven

`explorer-workspace-tree.component.html` renders exactly five hand-written
`<app-tree-row>` elements per project (Board, Project Hub, Wiki, Backlog,
Epics - see the `ASS-658/ASS-597` comment, which still says "exactly four"
even though Wiki was added later without updating it). There is no array/model
today driving this list. Adding URL rows means: add `Urls` to whatever feeds
`ProjectSidebarRow`/`buildProjectSidebarRows()` (`studio-shell.project-rows.ts`),
then add a `@for` loop after the five literal rows. Click handling for the new
rows should NOT go through `StudioTabStateService`/`@Output()` request
emitters like the other five (those all open a tab) - it should just open the
URL (`window.open(url, '_blank')`), since these are plain external links.

## Shipped direction: embedded live preview

Robert's own framing: *"das ist so ein bisschen perspektivischer Ausblick"* -
a direction layered on top of Project URLs. The basic embedded preview,
host-side readiness check, in-place settings, and owned process console have
shipped. Element selection and deeper orchestrator context remain future work.

**The idea:** clicking a Project URL's "Open" action opens it embedded in the
app's own main content area (VS Code's "Simple Browser" is the closest
existing analogue) instead of - or in addition to - a new external browser
tab. On top of that:
- **Auto-refresh mostly comes for free.** If the embedded content is the
  actual dev-server page, its own live-reload (`ng serve` / Vite HMR /
  `dotnet watch`) already refreshes it - no separate polling needed for that
  part.
- **A collapsible console/log drawer** shows the bounded stdout/stderr tail of
  the owned dev-server process. It is not the browser JavaScript console and
  it is deliberately separate from agent CLI task output.
- **The orchestrator becomes aware of what's on screen** - the currently
  embedded URL (and ideally page title/route) is ambient context for whatever
  you type next. Corrected after feedback: this does **not** mean a second
  orchestrator input duplicated under the preview - the orchestrator surface
  already lives elsewhere in the app (its own panel); the preview pane only
  needs to feed it "what URL/page am I looking at" as context, not host its
  own chat control.
- **Per-embed settings** edit URL, start command, working directory, and port
  without leaving the preview. The command remains explicit and configurable;
  Studio does not silently force a production serve mode.
- **One level further (Robert's explicit "next level"): reach into the page
  and hand elements to the orchestrator** - point at a rendered element (like
  an inspect-element picker) and pass its selector/markup as prompt context,
  the way v0 or Chrome DevTools' element picker work.

**Technical reality check** (worth being explicit about before this gets
planned in earnest):
- A plain `<iframe>` embedding another localhost port is a different origin
  even on the same machine, so the parent page **cannot** reach into its DOM
  (same-origin policy) - "reach into the iframe" is not free with a plain
  iframe, regardless of it being your own dev server.
- Basic embedding (view + auto-refresh via the framed page's own HMR) works
  today for most local dev servers, since dev tooling (Angular CLI, Vite,
  Kestrel) does not usually set restrictive `X-Frame-Options`/CSP
  `frame-ancestors` - but some pages (deliberately, or a public site) do, and
  embedding then simply fails. An always-available "open in a real external
  browser" escape hatch is required, not optional (same fallback VS Code's
  Simple Browser offers).
- The DOM element-picker ambition needs one of: (a) a small opt-in
  instrumentation script injected into the previewed app (feasible - these
  are the user's own dev projects) that runs a highlight/picker mode and
  `postMessage`s the selection back to the parent; or (b) replacing the plain
  iframe with a server-driven, CDP-controlled remote browser (Playwright,
  which this repo already has test infra for) that streams back a real DOM
  and supports genuine automation/inspection. (a) is far cheaper but only
  covers the user's own instrumented apps; (b) is heavier but general and
  reuses infrastructure that already exists in this repo for Playwright
  specs.

See the "Click a URL row" interaction in [`ui.html`](ui.html) for a rough
visual sketch of the layout (address bar, fake preview, collapsed console
drawer, orchestrator input bar with a context chip) - it is a sketch of the
*shape*, not a proposal for how the iframe/CDP question gets resolved.

## Resolved runtime contract

- HTTP readiness remains the liveness truth, including servers started outside
  Studio. A process snapshot answers the different question of whether Studio
  owns a command that can be inspected or stopped.
- A dedicated singleton owns dev-server sessions and a bounded output tail.
  Restart, URL/project removal, failed launch cleanup, and backend shutdown all
  terminate the complete process tree.
- Each URL keeps its own explicit command and optional CWD/port. Working
  directory resolution falls back from URL CWD to repository path, then project
  root path.

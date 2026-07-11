# Workstream

Status: Concept (living). Slices EW-1 and EW-2 implemented.

> Naming (operator decision 2026-07-09): user-facing, this frame is called
> **Workstream** and is pinned as the **top** element of every project wiki's
> content tree. The internal identifiers, the concept file name, and the physical
> folder (`docs/engineering-workstream/`) keep the historical
> `engineering-workstream` name so existing checkouts do not break; only the
> displayed label was remapped (see [§8](#8-display-name-and-tree-position)).

The Workstream is a fixed frame in every project's wiki that keeps
the development story in the same five places, in the same order, forever. It is
the answer to "where does knowledge about how this project is being built
actually live?" — so that operators and agents never have to guess, and never
reorganise it away.

## 1. Problem

Project knowledge tends to scatter. Status lives in one person's head, drift
notes land wherever, decisions are implicit, and the running history is a chat
log nobody re-reads. Every project re-invents its own layout, so moving between
projects means re-learning where things are. Worse, agents editing the wiki can
quietly delete or restructure the very scaffolding that made the knowledge
findable.

## 2. The fixed five-area frame

The frame is five areas that exist in every project wiki, always in this order:

| # | Area | Purpose |
|---|---|---|
| 01 | **Current Development State** | What is actively being built right now: in-flight streams, their intent, and where they stand. |
| 02 | **Development Signals** | The health readout: drift, regressions, recurring failures, and the metrics worth watching. |
| 03 | **System Knowledge** | How the system actually works: durable architecture, contracts, and hard-won operational lessons. |
| 04 | **Decision Log** | Why the system is shaped the way it is: decisions taken, alternatives rejected, and their triggers. |
| 05 | **Workstream Log** | The running narrative of the engineering workstream: what happened, in order, over time. |

Together they answer, in order: what now, how healthy, how it works, why it is
so, and what happened. The frame is deliberately small and never grows — five
areas is the whole vocabulary.

## 3. Physical model — the frame is real folders

Consistent with the wiki tree contract ([../contracts/wiki-tree.md](../contracts/wiki-tree.md)),
the frame is a real folder structure under the wiki root, not a virtual grouping
layer:

```
docs/engineering-workstream/
  00-overview.html                       <- frame orientation shell
  10-current-development-state/index.html
  20-development-signals/index.html
  30-system-knowledge/index.html
  40-decision-log/index.html
  50-workstream-log/index.html
```

Each area is a folder holding a landing **HTML shell** (`index.html`). The shells
are self-contained: inline design tokens mirroring the studio design system,
both light and dark via `prefers-color-scheme` (the wiki renders HTML in a
script-disabled sandboxed iframe, so theming must be CSS-only), and a bold
orientation layout that states each area's purpose and its place in the frame.

## 4. Immutability — the frame's shape is locked

The frame's shape is immutable, and the rule is enforced server-side so it holds
even when the request comes from an agent, not just the UI. Two tiers:

- **Structural lock** — the frame root, the five area folders, and the landing
  shells cannot be moved, renamed, or deleted. This keeps the five areas present
  and in order.
- **Content lock** — the landing shells additionally cannot be overwritten,
  because their orientation layout *is* the frame.

The single source of truth is
[`backend/Features/Docs/EngineeringWorkstreamFrame.cs`](../../backend/Features/Docs/EngineeringWorkstreamFrame.cs).
The wiki move, delete, and save endpoints consult it and reject blocked
mutations (`409 Conflict` for move/delete, a rejected save for content), and the
wiki tree tags each frame node with an `immutable` flag so the UI shows a lock
affordance and hides rename/delete/move.

### 4.1 EW-2 collector lifecycle

EW-2 adds an opt-in collector pair to the task pipeline. At task onboarding,
`pre-workstream-onboarding` ensures the EW-1 frame and replaces the bounded
`Current Development State/current.md` projection with the task that is about
to run. At settled completion, `post-workstream-collector` receives task,
status, diff, and aspect evidence and proposes updates to the frame. Both steps
are reporting-only and never influence the lane decision.

The completion model receives the complete five-area frame map, the known pages
already under the frame, each area's authoring rules, and the hard growth
budgets. Its JSON reply is only a proposal. The backend validates identities and
owns every filesystem write:

- Workstream Log receives one chronological outcome entry.
- Development Signals merge by stable identity and increment `frequency`.
- System Knowledge updates the identity page in place and always carries
  `Last Updated From` provenance, falling back to the task key when omitted.
- Decision Log changes only for an actual decision.
- Current Development State is replaced when active state changes.

These are implemented storage contracts, not future collector goals. In
particular, Decision Log proposals are persisted by identity, System Knowledge
cannot be written without the server-owned `Last Updated From` line, and the
single Current Development State projection is replaced rather than appended.

The two pipeline steps default off and are enabled per project. Activating
either step self-provisions the EW-1 frame before writing.

## 5. Subpages — where the actual content goes

Everything below an area folder is an ordinary wiki page with full git history.
Operators and agents add, edit, move, and delete subpages freely; only the frame
scaffolding is fixed. A decision is a subpage under `40-decision-log/`; a drift
write-up is a subpage under `20-development-signals/`. The frame gives the
address; the subpages carry the payload.

EW-2 makes the anti-overgrowth rules executable, not prompt advice:

- maximum 8 accepted proposals per collector run and 3 per area per run;
- maximum 40 Markdown pages per area;
- maximum 100 retained Workstream Log entries, newest first;
- maximum 4,000 characters per generated item;
- maximum subpage depth of 2 below an area, expressed as either `identity.md`
  or `group/identity.md`;
- no model-authored landing shells or additional top-level areas;
- update-by-identity is preferred and enforced for signals and system knowledge.

The prompt requires related existing subpages to be linked when applicable.
Weak, duplicate, or structurally invalid proposals are rejected individually,
while valid siblings still apply.

## 6. AGT-1984 — relocation into the wiki's own checkout

The wiki is moving to its own branch-bound checkout (AGT-1984) so wiki edits do
not entangle with source branches. The frame is built to move with it:

- The frame is anchored by a single wiki-root-relative constant
  (`EngineeringWorkstreamFrame.FrameRootRel`) and reasons only about
  wiki-root-relative paths. It never hard-codes the `docs/` prefix or an absolute
  checkout path.
- Resolving where the wiki root physically lives is already isolated in
  `ProjectDocsService` (the `WikiRel` base + `ProjectRepoResolver`). When the wiki
  gains its own checkout, only that resolution changes; the frame definition,
  its area slugs, and its immutability rules move unchanged.

In other words, the frame is checkout-agnostic by construction: it is a set of
relative paths plus rules over them, which is exactly what survives a relocation.

## 7. Slices

- **EW-1:** the fixed five-area frame - immutable folders and
  landing shells, the self-contained HTML orientation shells, the tree
  `immutable` flag, server-side move/delete/save enforcement, and navigation. No
  automated authoring of subpage content yet.
- **AGT-2024 (this slice): self-provisioning the frame into target projects.**
  The frame is materialized into a watched project's `docs/` automatically, the
  first time a wiki-writing pipeline step runs for it. There is no manual
  bootstrap action and no "onboarded" flag (operator decision 2026-07-10):
  activating a wiki-writing step is what creates the structure. See
  [§9](#9-self-provisioning-agt-2024).
- **EW-2:** opt-in task-onboarding and completion collector, area-specific
  merge/update rules, prompt-known pages, mandatory provenance, and hard
  anti-overgrowth budgets. A later curator may improve editorial quality without
  changing these storage limits.
- **EW-3:** a gated, exactly-once retro pilot classifies existing task history
  into an initial state, signal, knowledge, decision, and log baseline. A
  default-off periodic curator then verifies collector-owned pages, merges
  duplicate canonical keys, caps retained evidence, and prunes only empty,
  low-confidence overflow. See [section 10](#10-retro-pilot-and-curator-ew-3).

## 8. Display name and tree position

The 2026-07-09 rename is a presentation-layer change, deliberately kept off the
frame's identity so it stays relocatable (see [§6](#6-agt-1984--relocation-into-the-wikis-own-checkout)):

- **Display name.** The frame root's wiki-tree label is
  `EngineeringWorkstreamFrame.RootDisplayName` (`"Workstream"`), applied in
  `ProjectDocsService.BuildTreeNodes` via `EngineeringWorkstreamFrame.DisplayTitle`.
  Only the root is relabelled; the five areas keep the titles derived from their
  own folder names.
- **Physical folder unchanged.** The on-disk folder stays
  `docs/engineering-workstream/` (`FrameRootRel`), so existing checkouts, the
  immutability rules, and every wiki-root-relative path keep working. A physical
  folder rename, if ever wanted, is a separate migration and is out of scope here.
- **Top of the tree.** The frame root is pinned first in
  `ProjectDocsService.CompareTreeNodes` (`EngineeringWorkstreamFrame.IsFrameRoot`
  sorts ahead of all other `docs/` siblings), so the Workstream always leads the
  wiki content tree regardless of alphabetical order.

## 9. Self-provisioning (AGT-2024)

The frame is not something an operator bootstraps by hand, and there is no
"not onboarded" state to leave. Instead the frame is **self-provisioned**: when a
wiki-writing pipeline step is active for a project, that step creates the frame
structure the first time it runs (operator decision 2026-07-10).

- **The ensure-frame primitive.** `EngineeringWorkstreamFrameSeeder.EnsureFrame`
  idempotently materializes the whole frame (the five area folders, their landing
  shells, and the overview shell) into a target project's `docs/`. It only ever
  creates the six known shells and their folders, so foreign files are always
  untouched; it never overwrites an existing shell, so a partial frame is
  completed and a full frame is a no-op; and it never throws, so a seed hiccup can
  never break the step that called it.
- **Wired into the steps.** Every wiki-writing step calls the primitive before its
  own writes: today `WikiMaintenancePostStepRunner` and
  `WikiLearningsPostStepRunner`, later the EW-2 collector and curator. Because the
  step being enabled is the provisioning trigger, the old "skip when the project
  has no wiki" guard is gone: an enabled step now bootstraps its own home under
  `docs/`.
- **Content source.** The seeded shells are rendered by
  `EngineeringWorkstreamFrameContent` from `EngineeringWorkstreamFrame.Areas`, so
  the seeded frame can never drift from the declared frame identity and meets the
  same self-contained / both-themes / orientation-layout invariants as the
  hand-authored EW-1 shells (`EngineeringWorkstreamFrameContentTests`).
- **Language.** Frame pages for public / open-source repos are English throughout;
  an internal project may opt into a localized frame. The choice is the
  `ProjectSettings.WorkstreamFramePublic` setting with a heuristic default
  (English), resolved by `WorkstreamFrameLanguageResolver`. The five area
  identities (folder slugs and titles) stay a fixed English vocabulary in every
  language; only the orientation copy is localized.

## 10. Retro pilot and curator (EW-3)

`WorkstreamCurationService` supplies the history bootstrap and the bounded
maintenance pass. `WorkstreamCuratorHostedService` owns the periodic schedule.
The hosted service is disabled by default with `WorkstreamCurator:Enabled`; this
is the EW-2 gate in deployments. Enabling it means collector-style Workstream
authoring is active for that project set.

The first enabled cycle scans historical task metadata and log tails once. A
versioned marker at `engineering-workstream/.curator/retro-pilot-v1.json` makes
the pass exactly-once and records its task count, taxonomy matches, and evidence
task ids. The initial Agent Studio taxonomy validates three known incident
families: `post-processing-robustness`, `restart-resume-orphans`, and
`reissue-wipe`. Matches generate initial pages in all five frame areas. An empty
history still records state and log pages, so repeated service restarts do not
continually reinterpret the same baseline.

The curator has a context separate from task and review-orchestrator state at
`.curator/context.json`. Every cycle records its last run and action counts.
Its mutation authority is deliberately narrow:

- Only Markdown pages with `managed-by: workstream-collector` may be changed.
  Operator pages and immutable HTML shells are never candidates.
- Duplicate `canonical-key` values merge only within the same frame area.
- Evidence is deduplicated and capped at 20 recent rows per page.
- Every managed page receives a `last-verified` timestamp.
- Each area is capped at 40 managed pages. Pruning applies only to overflow
  pages that have confidence below `0.5` and no evidence rows.

These limits are the anti-overgrowth contract. They ensure periodic maintenance
can compact accumulated collector output without turning the curator into a
general wiki deletion mechanism. Stable structured event names
`workstream-retro-pilot-completed`, `workstream-retro-pilot-failed`,
`workstream-curation-completed`, and `workstream-curator-cycle-failed` expose
the expensive history pass and every subsequent maintenance cycle.

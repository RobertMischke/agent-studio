# Engineering Workstream

Status: Concept (living). Slice EW-1 implemented.

The Engineering Workstream is a fixed frame in every project's wiki that keeps
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

## 5. Subpages — where the actual content goes

Everything below an area folder is an ordinary wiki page with full git history.
Operators and agents add, edit, move, and delete subpages freely; only the frame
scaffolding is fixed. A decision is a subpage under `40-decision-log/`; a drift
write-up is a subpage under `20-development-signals/`. The frame gives the
address; the subpages carry the payload.

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

- **EW-1 (this slice):** the fixed five-area frame — immutable folders and
  landing shells, the self-contained HTML orientation shells, the tree
  `immutable` flag, server-side move/delete/save enforcement, and navigation. No
  automated authoring of subpage content yet.
- **Later:** seeding the frame into other project wikis, subpage authoring
  conventions per area, and wiring signals/decisions/log entries from pipeline
  output.

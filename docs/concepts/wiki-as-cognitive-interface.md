# Wiki as a Cognitive Interface

The project Wiki is not only a reading surface. It is a bidirectional interface
between repository knowledge and executable project work. A page explains the
current model, and its page action bar provides the predictable path back into
tasks, lifecycle metadata, and the Orchestrator.

## AIP-4: pages have a backchannel

A page becomes operational when the reader can act without reconstructing its
identity elsewhere. The shared page action bar is that backchannel:

- **Create Task in Project** opens the standard task form with the canonical
  page reference and a bounded page excerpt already in the prompt.
- **Archive** keeps the source page in place and changes its companion
  classification to `archived`.
- **Open in Orchestrator Chat** opens the existing project chat and sends the
  page identity as navigation context.
- **Pin to Home** adds the page to a selected curated section in
  `docs/app/config/home.json`. Label and note start from page title and
  excerpt, remain editable, and the resulting Git change is shared with every
  operator and with agents that use the Overview for grounding. **Unpin from
  Home** removes only that curated entry.
- Page types can add one bounded action. A Workbench adds **Build as feature**;
  an incident or history page adds **Create follow-up**.

The four standard actions always remain in that order. Type-specific actions
come after them. This makes the position learnable across Wiki documents, concepts,
reports, incidents, and Workbenches.

## Personal star versus shared pin

The star and pin are separate concepts and never feed the same collection:

- **Star** means "Your personal shortlist." It is operator-local UI state,
  appears only in the Starred panel, and does not affect navigation or agents.
- **Pin to Home** means "Curated entry point for everyone." It changes the
  repository-owned `home.json`, appears in the curated Overview sections, and
  is versioned in Git.

The UI uses a star icon for the first and a pin icon for the second, states
those purposes in tooltips, and keeps pinned pages out of the Starred panel.

## Page identity

Every page projects one of five canonical UI types:

| Page type | Primary derivation | Icon |
|---|---|---|
| `doc` | Default for a readable repository document | file |
| `concept` | Companion classification or `concepts/` family | book |
| `workbench` | `workbench.json` registration or companion classification | eye |
| `incident` | Incident/history classification or path family | activity |
| `report` | Report/analysis classification or path family | list |

The raw curation type remains available for consolidation workflows. The
canonical page type is the smaller interaction vocabulary used by the tree,
page head, action bar, and chat context.

## Chat context decision

Page identity is embedded in the existing project chat as
`navigationContext`. It does not introduce a `page:` form of
`OrchestratorContextKey`.

This is the simpler contract because a page is the operator's current
navigation detail, not a separate durable conversation owner. Project chat
history therefore remains continuous while each first send from a new page
carries:

```text
currentPage: repository-page
pageRef: page:<PROJECT>/<docs-relative-path>
pageTitle: <title>
pageType: <doc|concept|workbench|incident|report>
pageExcerpt: <bounded plain text>
```

The same canonical `pageRef` is written into page-backed task prompts. This
lets a task card retain its knowledge origin after the page or chat is closed.

## Archive semantics

Archive is a metadata operation, not a file move or deletion. The endpoint
updates only the adjacent companion's `classification.status`, commits that
sidecar, and leaves the source path readable and linkable. Archived pages use a
quiet historical treatment and do not emit an acute signal.

## Home curation semantics

`PUT /api/projects/{projectName}/wiki/home/pins/{relPath}` is the only mutation
path for shared Overview curation. Pin requests carry `sectionTitle`, `label`,
and `note`; the selected section must already exist. Re-pinning moves or
updates the one path instead of creating duplicates. Unpin requests remove the
path from all sections. The endpoint commits only
`docs/app/config/home.json`; it does not change the page or personal stars.

## Visual pattern

The variants and light/dark token behavior are captured in the
[Visual StyleGuide page action Workbench](../quality/visual-styleguide-workbench-wiki/index.html).
The production component is
`frontend/src/app/features/project-detail/components/page-action-bar/`.

This contract connects the Visual StyleGuide Workbench direction with AIP-4:
the action bar is the visible return path from knowledge to coordinated action.

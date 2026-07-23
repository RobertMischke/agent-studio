# Wiki Tree, Rendering & Per-Doc History

The project-level **Wiki / Docs** rail renders the watched project's complete
`docs/` tree from one project-wide source. `wikiSourceBranch = null` preserves
the legacy checkout-backed behavior. A configured git ref such as
`origin/develop` reads tree, content, assets, Pulse inputs, and history from that
ref without switching the working tree.

Branch-backed reads resolve the ref to a commit and materialize `docs/` once as
a SHA-addressed read-only snapshot through the shared Git info cache path. Warm
navigation reuses that snapshot. The Wiki header reports the source branch and
short commit, so Stable and Dev never imply a source from their deployment
checkout.

There is no app-owned organization manifest, no virtual grouping layer, and no
compatibility shim for historical root-level pages. If the Wiki should show a
folder, that folder must exist under `docs/`.

- Backend surface: [`backend/Features/Docs/ProjectDocsEndpoints.cs`](../../../backend/Features/Docs/ProjectDocsEndpoints.cs),
  [`backend/Features/Docs/ProjectDocsService.cs`](../../../backend/Features/Docs/ProjectDocsService.cs),
  git lookups in [`backend/Features/Git/GitService.cs`](../../../backend/Features/Git/GitService.cs).
- Frontend surface: `frontend/src/app/features/project-detail/components/project-wiki-section/`
  with the tree model in `wiki-tree.ts` and the history panel in
  `wiki-doc-history/`.
- Wire shapes: [`frontend/src/app/models/project-docs.model.ts`](../../../frontend/src/app/models/project-docs.model.ts).

## Physical tree contract

The docs root is `<project repo>/docs`.

The tree endpoint recursively surfaces:

- folders that contain at least one visible document descendant,
- `.md` files as Markdown pages,
- `.html` and `.htm` files as sandboxed HTML pages,
- image assets only through the asset endpoint when referenced by a page.

Hidden entries (dot-prefixed names such as `.gitkeep`) are skipped. Empty
folders are pruned from the navigation tree.

Siblings are sorted folders first, then files. An optional leading numeric
prefix such as `01-`, `01_`, or `01.` controls sort order and is hidden from the
display title. Without a prefix, items sort by display title. A saved category
drag-order (`docs/app/config/wiki-order.json`) overrides the folder order per sibling
group; unlisted folders sort behind in the default order.

There is no pinned node and no immutable node: every folder and page follows
the same sort, rename, move, and delete rules. (The former Engineering
Workstream frame — a pinned `docs/engineering-workstream/` root with locked
folders and shells — was retired 2026-07-19; see the hardening chronicle
[workbenches/haertung-verteilte-ausfuehrung/historie.html](../../operations/haertung-verteilte-ausfuehrung/historie.html)
for the historical record.)

The display title for a document is its first H1 when present; otherwise it is
the file name without extension and without the optional order prefix.

## Pulse drift groups and the `human-action` convention

The wiki Pulse drift bar grades the **real top-level `docs/` folders**: every
top-level folder that holds at least one page is a drift group (first path
segment = group; the group title is the folder name without its order prefix).
Folders without pages do not appear; group order follows the saved
`docs/app/config/wiki-order.json` root order, unlisted folders behind in the tree's
default order (numeric `NN-` prefix, then name). Pages
directly at the docs root belong to no group. The Pulse change-feed area badge
uses the same top-folder mapping.

The **`human-action` signal is a folder-independent frontmatter convention**:
any wiki page (every document type the tree surfaces is scanned; in practice
the signal lives in Markdown frontmatter), wherever it lives, that carries
frontmatter with

```yaml
human-action: <what a human should do>
status: observed   # or: active
```

raises a Pulse warning (`kind: human-action`) until its `status` leaves
`observed`/`active` (e.g. becomes `resolved`). The `human-action` value is the
action text shown to the operator.

## Page lifecycle frontmatter

Designs, concepts, and explorations opt into one shared lifecycle by carrying
the fields defined in
[`wiki-page-lifecycle.schema.json`](../../app/schemas/wiki-page-lifecycle.schema.json):

```yaml
---
lifecycleSchema: wiki-page-lifecycle/v1
pageKind: exploration
lifecycleState: review-requested
editedBy: "Robert"
editedAt: 2026-07-21T05:46:33Z
lifecycleHistory:
  - state: review-requested
    editedBy: "Robert"
    editedAt: 2026-07-21T05:46:33Z
    note: "Options are ready for a decision."
---
```

The states are `in-progress`, `review-requested`, `decided`, and `done`.
`editedBy` and `editedAt` describe the lifecycle edit, not an inferred Git
author. Every transition appends a history entry and updates the current state,
editor, and timestamp together.

This frontmatter is the lifecycle source of truth for Markdown. The adjacent
`.meta.json` companion remains authoritative for grading, consolidation
classification, and task links and must not copy lifecycle fields. HTML cannot
carry leading YAML, so a Workbench uses the same field names and values in its
single `workbench.json` descriptor (`schemaVersion: 2`). Pulse normalizes both
authoring shapes into one projection and groups them by the same state machine.

## API endpoints

All paths are rooted at `/api/projects/{projectName}/wiki`. `{projectName}` is a
`WatchPaths` entry name; an unknown project yields `404`. Wiki paths are always
relative to `docs/`, must not contain `..`, and must not be rooted.

### `GET /wiki/tree`

Returns the recursive physical docs tree.

```jsonc
{
  "projectName": "Agent Task Processor",
  "baseDir": "C:/repo/docs",
  "exists": true,
  "source": {
    "mode": "branch",
    "branch": "origin/develop",
    "commit": "8d10db4e...",
    "shortCommit": "8d10db4e",
    "writable": false,
    "error": null
  },
  "root": [
    {
      "name": "architecture",
      "title": "architecture",
      "relPath": "architecture",
      "type": "folder",
      "children": [
        {
          "name": "model.md",
          "title": "Architecture Model",
          "relPath": "architecture/model.md",
          "type": "md",
          "children": []
        }
      ]
    }
  ]
}
```

### `GET /wiki/files/{relPath}`

Reads a Markdown or HTML page from the physical docs tree. HTML pages are served
as content and rendered by the frontend in a script-enabled sandboxed iframe.
The iframe grants only `allow-scripts`; without `allow-same-origin` it receives
an opaque origin and cannot inherit Studio's origin or directly access its
cookies, storage, or DOM. Network requests remain subject to normal browser and
CORS policy; this sandbox is not a network-deny boundary.

### `GET /wiki/assets/{relPath}`

Streams a referenced image or diagram asset. This is intentionally limited to
image-like extensions so wiki pages can render relative screenshots without
turning the endpoint into arbitrary file serving.

### `GET /wiki/history/{relPath}`

Returns per-document provenance and Git history.

```jsonc
{
  "relPath": "architecture/model.md",
  "model": "claude-opus-4-8",
  "metadata": {
    "model": "claude-opus-4-8",
    "updatedAt": "2026-06-09",
    "reason": "distil run learnings",
    "taskKey": "ASS-1709",
    "status": "active",
    "runCount": "3",
    "hasFrontmatter": true
  },
  "commits": [
    {
      "sha": "8d10db4e...",
      "shortSha": "8d10db4e",
      "authorDateUtc": "2026-06-09T20:03:16Z",
      "author": "Crash Recovery",
      "subject": "Wiki: improve navigation",
      "filesChanged": 15,
      "added": 1905,
      "removed": 87
    }
  ]
}
```

Provenance precedence: frontmatter `model:` / `last-distilled:` / `why:` wins;
when absent, `model` falls back to the `Co-authored-by` trailer of the most
recent commit that touched the file. `commits` is empty when the repo root
cannot be resolved, but frontmatter still renders.

### `GET /wiki/revisions/{sha}/{relPath}`

Returns the content of a Markdown or HTML page at a past commit so the history
panel can preview old revisions.

### `POST /wiki/pages`

Creates a real `.md`, `.html`, or `.htm` page under `docs/` and commits it into
the project repository.

### `POST /wiki/folders`

Creates a real folder under `docs/`, seeds `.gitkeep`, and commits it. The folder
appears in the Wiki once it contains at least one visible page.

### `POST /wiki/move`

Moves or renames a real file or folder through `git mv` and commits the change.
This is how Wiki organization changes are made: move the actual path.

### `DELETE /wiki/files/{relPath}`

Deletes a real file or folder through `git rm` and commits the change.

## Write policy

Checkout-backed Wikis retain commit-backed page edits, creates, moves, deletes,
and uploads. A branch-backed Wiki is deliberately read-only: all mutation
endpoints reject the operation with an explicit divergence-prevention message,
and the UI disables the corresponding controls. Operators switch the project
setting back to Checkout before writing. The application never guesses a write
branch and never writes into one checkout while displaying another ref.

## Frontend behavior

The Wiki behaves like an app inside the app:

- only one document is open at a time,
- the left navigation and right context panel can be collapsed and resized,
- collapsed / width / selected-page state survives F5 through local storage,
- the filter is subtle and opt-in,
- right-click on files and folders opens a text-only context menu,
- the context rail shows file path, metadata, history, linked-doc information,
  and drift actions.

## File organization rule

When a page feels lost, move it into a better real folder. Do not create virtual
categories, manifest nodes, or root-level back-compat pages. The repository tree
is the Wiki tree.

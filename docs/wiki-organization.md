# Wiki Organization, Hierarchy & Per-Doc History

The project-level **Wiki / Docs** rail (`project-shell` → Config → Steering Docs
→ Wiki / Docs) renders the watched project's `docs/` tree. It is no longer a
flat folder list: docs can be grouped into user-created themes, nested into a
hierarchy, reordered by drag-and-drop, renamed via a text-only right-click
menu, and inspected for provenance + git history per document.

- Backend surface: [`backend/Endpoints/ProjectDocsEndpoints.cs`](../backend/Endpoints/ProjectDocsEndpoints.cs),
  [`backend/Services/ProjectDocsService.cs`](../backend/Services/ProjectDocsService.cs),
  git lookups in [`backend/Services/GitService.cs`](../backend/Services/GitService.cs).
- Frontend surface: `frontend/src/app/features/project-detail/components/project-wiki-section/`
  (tree model in `wiki-tree.ts`, history panel in `wiki-doc-history/`).
- Wire shapes: [`frontend/src/app/models/project-docs.model.ts`](../frontend/src/app/models/project-docs.model.ts).

## Design: a virtual layer over an immutable tree

The physical `docs/` tree and its per-file git history are the source of truth
and are never mutated by organisation actions. Grouping, nesting, ordering, and
title overrides live in a single git-tracked manifest at the docs root:
`docs/.wiki-organization.json`. Nodes reference docs by their `relPath`, so the
underlying files never move — per-file git history (which the History panel
reads) stays pristine, and a doc that the manifest does not place falls into a
synthetic **Ungrouped** bucket in the UI.

The dotfile is not a `*.md` document, so it never appears as wiki content. A
corrupt or unreadable manifest degrades to empty rather than throwing, so a bad
write can never brick the wiki view.

## API endpoints

All paths are rooted at `/api/projects/{projectName}/wiki`. `{projectName}` is a
`WatchPaths` entry name; an unknown project yields `404`. Markdown reads are
guarded against path traversal (no `..`, no rooted paths, `.md` only for docs).

### `GET /wiki/organization`

Returns the user-defined organisation manifest. When no manifest has been
written yet it returns a valid empty manifest (`{ "version": 1, "nodes": [] }`)
rather than `404`; `404` means the project itself is unknown.

```jsonc
{
  "version": 1,
  "nodes": [
    { "id": "g-1a2b", "type": "group", "title": "Architecture",
      "relPath": null, "parentId": null, "order": 0 },
    { "id": "doc:design/overview.md", "type": "doc", "title": "Overview",
      "relPath": "design/overview.md", "parentId": "g-1a2b", "order": 0 }
  ]
}
```

### `PUT /wiki/organization`

Replaces the manifest with the JSON body (same shape as the GET response) and
returns the sanitised, persisted manifest. The server sanitises every write
(`ProjectDocsService.SanitizeOrganization`): nodes without an `id` or with an
unknown `type` are dropped, titles are trimmed, a `relPath` on a `group` node is
cleared, the manifest is capped at 5000 nodes, and `version` is normalised to
`1`. Front-end mutations (rename, new group, move, delete, remove-from-group)
are optimistic and reconcile against this returned manifest.

### `GET /wiki/history/{relPath}`

Per-document provenance + git history. `{relPath}` is the doc path relative to
the docs root (markdown only). `404` when the file is missing or the path is
rejected.

```jsonc
{
  "relPath": "design/overview.md",
  "model": "claude-opus-4-8",          // frontmatter `model:` wins, else the
                                        // latest commit's Co-authored-by trailer
  "metadata": {                         // parsed from the doc's YAML frontmatter
    "model": "claude-opus-4-8",
    "updatedAt": "2026-06-09",          // last-distilled / last-updated / date
    "reason": "distil run learnings",   // why / reason / summary
    "taskKey": "ASS-1709",
    "status": "active",
    "runCount": "3",
    "hasFrontmatter": true
  },
  "commits": [                          // file's git log, newest first (max 50)
    {
      "sha": "8d10db4e…", "shortSha": "8d10db4e",
      "authorDateUtc": "2026-06-09T20:03:16Z",
      "author": "Crash Recovery", "subject": "Wiki aufwerten: …",
      "filesChanged": 15, "added": 1905, "removed": 87
    }
  ]
}
```

Provenance precedence: a doc's frontmatter `model:` / `last-distilled:` /
`why:` win; when absent, `model` falls back to the `Co-authored-by` trailer of
the most recent commit that touched the file (managed runs stamp the model
there). `commits` is empty when the repo root cannot be resolved; the panel
still shows frontmatter provenance in that case.

## Manifest schema — `docs/.wiki-organization.json`

Single JSON object, git-tracked, at the docs root of the watched project.

| Field | Type | Notes |
|---|---|---|
| `version` | int | Manifest version. Always `1` today. |
| `nodes` | array | Flat list of nodes; hierarchy is expressed via `parentId`. |

Each node:

| Field | Type | Notes |
|---|---|---|
| `id` | string | Required, unique. Groups use a generated `g-<uuid>` id; docs use `doc:<relPath>`. |
| `type` | `"group"` \| `"doc"` | `group` = a user-created theme; `doc` = a pinned `docs/` file. Any other value is dropped on write. |
| `title` | string \| null | Group name, or a doc title override. Trimmed; null on a doc means "use the file's own H1/name". |
| `relPath` | string \| null | Doc path relative to the docs root (forward slashes). `null`/cleared on a group. |
| `parentId` | string \| null | Parent node `id`; `null` for a root-level node. Deleting a group re-parents its children to the group's own parent. |
| `order` | int | Sort order among siblings sharing the same `parentId`. |

A `doc` node only needs to exist when the user has placed or renamed it; any
`docs/*.md` file with no matching node renders under the synthetic
**Ungrouped** group and is not written to the manifest until it is organised.

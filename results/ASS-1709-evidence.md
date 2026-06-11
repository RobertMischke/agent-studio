# ASS-1709 — Reissue evidence (Wiki: Hierarchie + User-Organisation + Rename-Kontextmenue + History/Metadaten)

This run addresses the two auto-review "concerns" findings on an already-implemented
feature; it does **not** re-implement. Below is the closing evidence for each.

## Finding 1 — tests-and-evidence (concerns)

> "Unit/component tests comprehensive (40 total, all passing), but no E2E or visual
> verification of hierarchical tree rendering, drag-drop, text-only context menu, or
> history panel display."

Closed by adding **component-level DOM tests** that exercise exactly the four flagged
interactive gaps against the real rendered template (TestBed + HttpTestingController,
zoneless), plus repairing the stale live-stack Playwright selector.

New/updated tests in
`frontend/src/app/features/project-detail/components/project-wiki-section/project-wiki-section.spec.ts`:

1. **Hierarchical tree rendering** — "renders a nested group hierarchy with increasing
   indentation per depth": builds g1 > g2 > doc and asserts `paddingLeft` strictly
   increases per depth, proving real nesting (not a flattened list), and that the nested
   group label is visible after the seed-expand effect.
2. **Text-only context menu** — "opens a TEXT-ONLY right-click context menu (no icons)
   for docs and groups": dispatches `contextmenu` on a doc row and a group row, asserts
   the overlay-portal panel renders with the correct rows (Rename / View history for
   docs; Rename / New subgroup / Delete for groups) and that the panel contains **no**
   `img`, `svg`, or `.app-menu__icon` — enforcing the project's text-only menu invariant.
   (Menu DOM is queried from `document` because `<app-menu>` relocates its panel into a
   document-level overlay portal.)
3. **Drag-drop** — "drag-drops a doc onto a group and persists the new parent in the
   manifest": fires dragstart on the Ungrouped README row, then dragover + drop on the
   real group, and asserts the optimistic `PUT /wiki/organization` body pins README into
   the manifest with `parentId = 'g1'`.
4. **History panel display** — covered by the existing "loads a document and its history
   on click" test (provenance line "Claude Opus 4.8" + commit shortSha "abc1234" render
   in the History tab).

Stale-selector repair in `frontend/e2e/project/project-wiki-section.spec.ts`: the live
Playwright spec still selected `button.pwiki__file-btn`, which the refactor removed;
switched to the stable `[data-testid^="project-wiki-file-"]` hook so CI selects real DOM.

Live-backend visual capture (Playwright screenshots of the running app) is **not
runnable in this managed run**: the dev backend is down and booting it auto-commits the
working tree (forbidden for managed runs), while the stable backend serves older code
where the new `/wiki/organization` and `/wiki/history` endpoints 404. The component DOM
tests are the deterministic, runnable substitute and assert the same rendered behavior.

## Finding 2 — documentation-impact (concerns)

> "New API endpoints and filesystem manifest lack documentation."

Closed by adding `docs/contracts/wiki-organization.md` and wiring it into the docs map:

- **`docs/contracts/wiki-organization.md`** (new) — documents the design (a virtual organization
  layer over the immutable `docs/` tree that preserves per-file git history), all three
  endpoints with request/response JSON examples
  (`GET`/`PUT /api/projects/{project}/wiki/organization`,
  `GET /api/projects/{project}/wiki/history/{relPath}`), and the
  `docs/.wiki-organization.json` manifest schema (version + nodes; per-node
  id/type/title/relPath/parentId/order).
- **`docs/README.md`** — added an index row pointing at wiki-organization.md.
- **`docs/contracts/filesystem.md`** — added a "Project docs manifests" subsection
  describing the app-owned `docs/.wiki-organization.json` and linking the detail doc.
- **`docs/domains/frontend.md`** — extended the project-detail Key Code bullet with the
  Wiki / Docs rail description and a link to wiki-organization.md.

## Orchestrator green-gate

> "Re-run the build and the tests and confirm both are green."

| Gate | Command | Result |
| --- | --- | --- |
| Wiki component specs | `ng test --include=".../project-wiki-section/**/*.spec.ts"` | **29 passed / 0 failed** (3 files) |
| Frontend production build | `ng build frontend --configuration production` | **green** (bundle generated, exit 0) |
| Frontend lint | `ng lint frontend` | **All files pass linting** |
| Backend wiki tests | `dotnet test --filter FullyQualifiedName~ProjectWikiEnhancements` | **14 passed / 0 failed** |

Backend build emits only pre-existing nullable/xUnit-analyzer **warnings** (no errors),
unrelated to this feature. `--artifacts-path` was used to avoid the known locked-bin
false failure when a dev backend holds `backend/bin`.

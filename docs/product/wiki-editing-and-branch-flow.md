# Wiki Editing And Branch Flow

The Wiki should behave like an in-app documentation workbench: reading is
immediate, reports explain metadata, and editing is explicit enough that users
understand which branch receives the change.

## Current Slice

The first implementation slice keeps the risk low:

- Markdown documents can enter an explicit Edit mode.
- The editor uses the existing TipTap-backed Markdown rich editor.
- Autosave is disabled for Wiki editing so a short typing pause does not create
  a git commit.
- Saving writes the file through the Wiki API and commits the changed document
  to the current checkout branch.
- The save result returns the branch and commit SHA so the UI can show what
  happened.

HTML and JSON pages remain readable through Document, Report, and Source. They
can be edited later through source editing or specialized metadata forms.

## GitHub Comparison

GitHub makes three things visible before the user commits a documentation edit:

| GitHub pattern | Wiki equivalent |
|---|---|
| The file path is always visible above the editor. | The Wiki reader header keeps the docs-root-relative path visible. |
| The current branch is part of the editing context. | The Wiki save result shows the branch; a later pre-save bar should show it before saving. |
| The user writes or accepts a commit message. | The current slice uses `wiki: update <path>` automatically. A later dialog should allow editing the message. |
| Protected/default branches may create a branch or pull request. | The product needs an explicit policy before editing on `main` or `develop`. |

## Recommended Branch Policy

Use a three-mode policy instead of hiding git behavior:

| Mode | When | Behavior |
|---|---|---|
| Current branch | Local worktree branch, task branch, or operator branch. | Save commits directly to the current branch after confirmation. |
| New docs branch | Current branch is protected, shared, or not writable. | Create a branch like `docs/wiki-edit/<date>-<slug>` and commit there. |
| Draft only | User wants to stage text without git mutation. | Save a draft artifact, show diff, and let the user commit later. |

The first mode is enough for local branch work. The second and third modes are
the next product decision because they affect task automation, review, and push
behavior.

## Proposed Save Dialog

The final save flow should show a compact dialog before the first commit in an
edit session:

| Field | Purpose |
|---|---|
| Document | Shows the docs-root-relative path. |
| Branch | Shows current branch or worktree branch, with a warning for protected branches. |
| Commit message | Defaults to `wiki: update <path>` but can be edited. |
| Save target | Current branch, new docs branch, or draft only. |
| Diff summary | Added/removed lines for the document before committing. |

Subsequent saves in the same edit session can reuse the chosen target unless
the branch changes.

## UI Shape

The document header should keep reading and editing modes calm:

- `Document` and `Report` are primary tabs.
- `Source` stays available as a secondary inspection tab.
- `Edit` is a right-side action, not another permanent reading tab.
- Unsaved changes should show a subtle dot and block navigation only when the
  user would lose local edits.
- After save, the UI should show `Committed on <branch> at <sha>`.

## API Direction

The current API can evolve in place:

```http
PUT /api/projects/{projectName}/wiki/files/{relPath}
{
  "content": "...",
  "commitMessage": "wiki: update docs/product/example.md",
  "targetMode": "current-branch"
}
```

Recommended response:

```json
{
  "relPath": "product/example.md",
  "saved": true,
  "changed": true,
  "sha": "abc1234",
  "branch": "codex/project-navigation-headers-20260611",
  "targetMode": "current-branch"
}
```

The API should reject unsafe paths, unsupported extensions, and branch names
that fail git validation. It should also distinguish `unchanged` from `changed`
so the UI can avoid fake commits.

## Open Decisions

| Decision | Recommendation |
|---|---|
| Should saving to `main` or `develop` be allowed? | Show a confirmation or force a new docs branch. |
| Should every save create a commit? | No. Use explicit save, no autosave commits. |
| Should source editing exist? | Keep Source read-only for now; add source edit only for HTML/JSON when the commit dialog exists. |
| Should reports be editable? | Reports are generated or curated HTML. Let them be opened and edited only in source mode later. |
| Should metadata JSON have forms? | Yes, but after the report and usage model stabilize. |

# Onboarding a new project

This page covers attaching an additional watched project to a running agent-orchestrator instance. The board can drive any number of projects in parallel (one sequential pipeline per project; see [../../AGENTS.md](../../../AGENTS.md) "Product Goal & Non-Goals").

> **For a normal new project, use the in-app "Onboard Project" dialog** (workspace sidebar "+" -> "+" on a workspace), not the steps below. It calls the project registry API (`POST /api/projects`, ADR-0042/ADR-0046) and needs no backend restart. See [getting-started.md](./getting-started.md) step 3. The `WatchPaths` flow documented on this page is now a legacy bootstrap-only mechanism (still real, still supported, but the harder path) - useful for scripted setups or when you deliberately want an explicit `.orchestrator.yml` project key.

## Prerequisites

- The backend is already running locally (`./api.sh start`, see [getting-started.md](./getting-started.md)).
- The target project lives in a Git working tree on disk. The board only watches checked-out repositories; bare repos and remote URLs are not supported.
- You know the project's CLI working directory. This is the path the agent's `cwd` will be set to when it picks up a task.

## Step 1 - Add a `WatchPaths` entry

The watch list lives in `backend/appsettings.Local.json` (gitignored, per-checkout). Two shapes are supported.

### Self-contained repo (the common case)

The Git repo *is* the CLI working directory and the project state lives in `agent-taskboard-workspace/projects/<derived-key>/`.

```json
{
  "WatchPaths": [
    {
      "Name": "Lotta Dashboard",
      "RootPath": "C:\\Projects\\Lotta\\Dashboard"
    }
  ]
}
```

`RootPath` is the CLI's `cwd` and, by default, becomes the project key. Stick to alphanumerics + `-` in the folder name; spaces produce ugly slugs.

### Repo with an `.orchestrator.yml` pointer

When the project wants an explicit, stable project key (for example a monorepo with several apps that should each land on the board as a separate project), drop an `.orchestrator.yml` at `RootPath`:

```yaml
# C:\Projects\Lotta\Dashboard\.orchestrator.yml
projectKey: lotta-dashboard
```

The scanner reads `projectKey` and resolves jobs under `<TaskRepository>/projects/<projectKey>/` instead of deriving the folder name. See [../../backend/Services/Jobs/JobScannerService.cs](../../../backend/Services/Jobs/JobScannerService.cs) (`OrchestratorPointer`).

### Repo and CLI cwd are not the same folder

When the Git repository root differs from the CLI working directory (monorepo with an app under a parent repo), add `RepositoryPath`:

```json
{
  "Name": "Runbook",
  "RootPath": "C:\\Projects\\Runbook\\App",
  "RepositoryPath": "C:\\Projects\\Runbook"
}
```

Git status, diff, commits, and the VS Code handoff use `RepositoryPath`; `RootPath` is still where the agent runs. When omitted, both fall back to `RootPath` (and `git rev-parse --show-toplevel` discovers the actual repo root).

## Step 2 - Restart the backend

**Today this is mandatory.** Config hot-reload makes the project visible at `GET /api/watch-paths`, but `TaskRunnerService` only creates per-project runners at startup, so `PUT /api/runner/<project>/mode` returns `400 Invalid project or mode` for the new project until the backend restarts.

```sh
./api.sh restart
```

Tracked for a durable fix as `fix-runner-mode-rejects-newly-added-projects` (see [../../.agents/skills/task-api/references/known-pitfalls.md](../../../.agents/skills/task-api/references/known-pitfalls.md) §4). Once that lands you can skip this step.

## Step 3 - Pick the project in the UI

Open `http://localhost:4010`, then use the project switcher in the header. The new project appears as soon as `/api/watch-paths` lists it. The lane structure (`0-backlog`, `1-preparation`, `2-ready`, ...) is created lazily under `agent-taskboard-workspace/projects/<projectKey>/` the moment the first task is created; no extra bootstrap is needed.

## Step 4 - Set defaults and queue the first task

Per-project preferences are persisted in `<TaskRepository>/project-settings.json` (see [../../backend/Services/ProjectSettingsService.cs](../../../backend/Services/ProjectSettingsService.cs)). The relevant ones today:

| Setting | Where to set | Default | What it does |
|---|---|---|---|
| `RunnerMode` | Project switcher / header pause toggle, or `PUT /api/runner/<project>/mode` | `manual` | `auto-continuous` lets the runner pick up `2-ready` jobs without a manual click. |
| `AutoCommit` | Project settings drawer | `true` | Stamps the lane-transition commit when a run moves `3-progress -> 4-auto-review`. |
| `OrchestratorModel` | Project settings drawer | `claude-opus-*` | Model the orchestrator uses when it decides on the user's behalf (re-issue / accept / escalate). |
| `AutonomyLevel` | Project settings drawer | `2` (balanced) | ADR-0026 autonomy scale for the orchestrator-prep loop. |

`DefaultAgent` is **not** persisted per project today; the agent / CLI / model is chosen per job at create time. If you find yourself setting the same `cliType` on every task for a given project, raise that as a feature request rather than working around it in scripts.

For the first task, follow [your-first-task.md](./your-first-task.md). The Task API skill ([../../.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md)) is the right path when you script the creation rather than clicking through the dialog.

## What to expect after pickup

- The runner walks the job through `2-ready -> 3-progress -> 4-auto-review`. The lane catalog and state strings are in [../filesystem-contract.md](../../contracts/filesystem.md).
- `4-auto-review` is the machine lane: the `ReviewDecisionOrchestrator` decides re-issue / accept-as-done / escalate. `5-human-review` is the lane that waits for you.
- The Activity Log streams live from the CLI via SignalR. Per-CLI frame semantics live in [../cli-skills/](../../cli/skills).
- Auto-commit (when enabled) stamps the `3-progress -> 4-auto-review` transition. Push is **not** automatic; see [../commit-push-doctrine.md](../git/commit-push-doctrine.md).
- Auto-review may decide to re-issue. The reissue lands the job back in `2-ready order=0`, not `3-progress` (fixes the dual-3-progress race; see [troubleshooting.md](./troubleshooting.md)).

## Removing a project

Delete the entry from `WatchPaths` and restart. The on-disk job folders under `agent-taskboard-workspace/projects/<projectKey>/` are **not** deleted; the board just stops watching them. Archive or move them by hand if you want them gone.

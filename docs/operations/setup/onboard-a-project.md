# Onboard a project

Project onboarding is a product workflow. It registers identity, creates the central task store, activates discovery, and optionally assigns execution. It does not put Agent Studio jobs in the product repository and it does not require a backend restart.

Project onboarding and [remote runner host onboarding](./linux-runner-host.md) are the two independent onboarding axes:

- Onboard a project to define what work exists, where its repository is, and where its tasks are stored.
- Onboard a host to define where work can execute. Then select that host as the project's execution runner.

## Before you start

Choose:

- A display name, such as `Quality Studio`.
- A unique 2 to 6 character short code, such as `QS`. Task keys use this prefix.
- A local Git checkout path, a repository URL, or both.
- The workspace that should contain the project.
- An optional execution runner. Onboard the host first if it is not listed yet.

For a local runner, the repository path must be an existing absolute Git checkout on the backend host. For a remote runner, provide the repository URL and let the runner's checkout workflow provide its local path.

## UI workflow

1. Open Agent Studio and find the target workspace in the Explorer.
2. Open its actions and choose **New project**.
3. Enter the name and short code.
4. Enter the local repository path and/or the HTTP(S) repository URL.
5. Select the local host or an onboarded execution runner.
6. Choose **Create project**.

The dialog closes after the server confirms creation. The project is immediately present on the board. No settings file edit or backend restart is needed.

## API workflow

Send `POST /api/projects`. `X-Client-Id` must identify a registered operator or automation client and is included in mutation telemetry. The bootstrap identity is `local-default`.

```sh
curl -i http://localhost:5030/api/projects \
  -H 'Content-Type: application/json' \
  -H 'X-Client-Id: local-default' \
  --data '{
    "displayName": "Quality Studio",
    "shortCode": "QS",
    "workspaceId": "ws-default",
    "repositoryPath": "C:\\Projects\\quality-studio",
    "repositoryUrl": "https://github.com/example/quality-studio",
    "executionRunner": "agent-runner-01"
  }'
```

`repositoryPath`, `repositoryUrl`, and `executionRunner` are optional. `repositoryUrl` must be an absolute HTTP(S) URL. For a board-only project, omit all three.

A successful request returns `201 Created` and the new `PROJ-NNN` record. A short-code collision returns `409 Conflict`; malformed values return `400 Bad Request`; an unknown workspace returns `404 Not Found`. Retrying a request after a `201` is safe in the sense that it cannot create a second project with the same short code: the retry receives `409`.

## What happens automatically

The server performs these steps in one onboarding workflow:

1. Allocates a monotonic registry id such as `PROJ-023` and initializes `NextTaskKeySeq` to `1`.
2. Persists the project in `<TaskRepository>/.metadata/projects.json` and assigns it to the requested workspace.
3. Creates `<TaskRepository>/projects/PROJ-023/tasks/` as the only task-store root for the new project. Lane and task directories are created on demand.
4. Stores the local checkout separately as `RepositoryPath`. It never uses `<repository>/.orchestrator/jobs` for a newly onboarded project.
5. Stores the repository URL as the well-known project URL with id `repo`.
6. Adds the project to the scanner and filesystem watcher from the registry, without adding a `WatchPaths` setting.
7. Creates a live local runner immediately when the local checkout exists, or persists the selected remote-runner assignment.

The board refresh after creation reads the same registry-backed source, so the new empty project is visible immediately.

## Store convention

New project task data always lives below the task-server workspace:

```text
<TaskRepository>/
  .metadata/projects.json
  projects/
    PROJ-023/
      tasks/
        lane and task data created by the task API
```

Never create or copy a task store into the product checkout. In-repository stores can expose prompts, logs, attachments, or operational metadata when the repository is pushed, and they mix product source with orchestration state.

TE, CAC, and CAR are documented legacy exceptions. Do not move them as part of ordinary onboarding. Their migration path is:

1. Pause pickup for the project and verify no task is running.
2. Back up the current in-repository store.
3. Copy it to `<TaskRepository>/projects/<PROJ-NNN>/tasks/`, preserving task folders and metadata.
4. Change the registry `StorageLocation` to the central path with a purpose-built migration tool.
5. Restart only if the migration tool reports that a legacy config watcher remains, verify task counts and task keys, then remove the old in-repository copy.

This migration is intentionally outside AGT-2144. Do not hand-edit registry metadata to perform it.

## Verify the onboarding

1. Confirm `GET /api/projects` contains the new id and `storageLocation` is `<TaskRepository>/projects/PROJ-NNN/tasks`.
2. Create a small task through the UI or task API and place it in Ready.
3. For an executable project, start or enable its runner and confirm the task reaches review.
4. Delete the test task.
5. Remove a disposable test project with `DELETE /api/projects/PROJ-NNN`. The endpoint removes its central project store and registry record.

## Troubleshooting

| Symptom | Cause and action |
|---|---|
| `409 shortCode ... is already used` | Choose another 2 to 6 character code. Codes are case-insensitively unique. |
| `400 repositoryPath ...` | Use an existing absolute local Git checkout. For a repository available only to a remote runner, omit the local path and provide `repositoryUrl`. |
| `404 Unknown workspaceId` | Read `GET /api/workspaces` and use a current workspace id. |
| Project exists but auto-pickup is unavailable | A board-only project has no local checkout, or the selected remote host is not ready. Set a valid repository path in Project Settings or complete [remote host onboarding](./linux-runner-host.md). |
| Project is missing from the board after `201` | Refresh once and inspect `GET /api/workspaces`. If the API listing is also missing, inspect structured `project-onboarded` and `watch-path-activated` log events. No restart should be required. |
| Old project uses `.orchestrator/jobs` | Treat it as legacy. Follow the migration outline above; do not onboard a duplicate project or push that folder. |

For the first real run, continue with [Your first task](./your-first-task.md). For general setup failures, see [Troubleshooting](./troubleshooting.md).

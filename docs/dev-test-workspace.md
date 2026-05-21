# DEV-Test-Workspace

Dedicated sandbox for automated UI probes and CLI-driven smoke tests.
Designed so probes can create tasks, watch agents work, and approve
verdicts without touching real codebases (Runbook, Agent Software
Studio, Lotta Dashboard).

## Layout

```
agent-taskboard-devspace/
├── test-repos/
│   └── playwright-test/        # ← isolated git repo, scratch/ gitignored
│       ├── .gitignore          (gitignores scratch/)
│       ├── README.md
│       └── App/
│           └── scratch/        # ← all probe deliverables land here
└── ...

agent-taskboard-workspace/      # shared workspace (dev + stable)
└── projects/
    └── playwright-test/        # ← lane folders + .orchestrator/ here
        ├── 0-backlog/
        ├── 1-preparation/
        ├── ...
        └── 7-archive/
```

The project is mounted in **both** dev (`backend/appsettings.Local.json`)
and stable (`agent-taskboard-stable/backend/appsettings.Local.json`)
as a `WatchPath` named `Playwright Test`. The workspace is shared, so
the same lane state is visible from either checkout.

## Configuration

- **Runner mode**: `manual` (set in `project-settings.json`). Probes
  must explicitly flip auto-pickup on. Stops the sandbox from running
  spurious agent cycles between probe runs.
- **AutoCommit**: `false`, **AutoPushStrategy**: `off`. The sandbox repo
  is local-only; nothing pushes to a remote.
- **No remote**: `test-repos/playwright-test/` has no `origin`; it's a
  pure local git init.

## Probe contract

Probes targeting this workspace should:

1. **Scope every task to `scratch/`**. The task prompt must explicitly
   restrict the agent to `App/scratch/<probe-name>/`.
2. **Wipe `scratch/` between runs** if the probe needs a fresh tree
   (the `.gitignore` makes this safe — nothing in `scratch/` is
   tracked).
3. **Use the dedicated testid lookups** (after F10/F9 ship):
   `getByTestId('studio-project-picker-Playwright Test')` for the
   picker, `getByTestId('create-submit')` for the dialog submit, etc.
4. **Always use the `Playwright Test` watch path** in the create-task
   dialog so the task folder lands under the sandbox project, not
   under Runbook.

## Resetting the sandbox

```sh
# Clear lane state (board view)
rm -rf agent-taskboard-workspace/projects/playwright-test/{0-backlog,1-preparation,1a-orchestrator-prep,1b-needs-human-review,2-ready,3-progress,3a-failed-pickup,4-auto-review,5-human-review,6-completed,7-archive}/*

# Clear deliverables
rm -rf test-repos/playwright-test/App/scratch/*

# Optional: re-init the lane skeleton
mkdir -p agent-taskboard-workspace/projects/playwright-test/{0-backlog,1-preparation,1a-orchestrator-prep,1b-needs-human-review,2-ready,3-progress,3a-failed-pickup,4-auto-review,5-human-review,6-completed,7-archive}
```

## When you might want a NEW probe project

Add another watch path next to `Playwright Test` if a probe needs:
- A different language stack the sandbox shouldn't carry.
- A long-lived state that survives wipes (rare; usually a finding to
  isolate the state instead).

Keep new probe projects under `test-repos/<name>/` with the same
"App + scratch + .gitignore" shape so the contract above applies
uniformly.

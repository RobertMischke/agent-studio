# Job State Reference

The canonical lane vocabulary. `targetState` strings on every mutation must
match one of these exactly; aliases are accepted but the resolved value is the
full form below. Source: `backend/Models/JobModels.cs::JobStates`.

| Lane | Constant | Purpose |
|------|----------|---------|
| `0-backlog` | `Backlog` | Long-term ideas; not yet prepared |
| `1-preparation` | `Preparation` | Being prepared by an agent (orchestrator-prep) |
| `1a-orchestrator-prep` | `OrchestratorPrep` | Orchestrator is preparing the task |
| `1b-needs-human-review` | `NeedsHumanReview` | Needs human attention before it can be queued |
| `2-ready` | `Ready` | Queued; runner picks from here in auto mode |
| `3-progress` | `Progress` | Actively running; max one per project |
| `3a-failed-pickup` | `FailedPickup` | Failed to start (spawn-failed, orphan, empty) |
| `4-auto-review` | `AutoReview` | CLI completed; aspect-runner verdicts pending |
| `5-human-review` | `HumanReview` | Aspect verdicts done; awaiting human acceptance |
| `6-completed` | `Completed` | Accepted; logically done (physically lands in `7-archive`) |
| `7-archive` | `Archive` | Final resting place |

## Aliases the server normalises

| Input | Resolves to |
|-------|------------|
| `completed`, `done`, `accepted`, `rejected`, `archived` | `6-completed` |
| `5-completed` (legacy) | `6-completed` |
| `ready` | `2-ready` |
| `progress` | `3-progress` |

Prefer the full `N-name` form for clarity in scripts.

## Notes

- `6-completed` and `7-archive` are logically the same place today; the system
  routes `completed` mutations into the archive folder. UI distinguishes them
  but on disk you will find your "completed" jobs under `7-archive/`.
- `3-progress` should always have at most one job per project. If you see two,
  it is the race-condition documented in
  `fix-auto-review-reissue-must-go-to-ready-not-progress` (2026-05-11).
- Empty shell folders (no `job.json`) cannot be moved via the API. Delete the
  folder directly via `fs.rmSync(path, { recursive: true })`.

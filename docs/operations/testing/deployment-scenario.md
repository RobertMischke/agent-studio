# Deployment regression scenario

The deployment scenario is the release-blocking, reduced end-to-end proof for
Task Server deployments. It uses one versioned definition at
`testsupport/scenario/deployment-scenario.json` and one runner at
`scripts/scenario.sh`. Deployment cards attach its Markdown report instead of
inventing a card-specific smoke test.

## What it proves

The fixed fixture contains a tiny Git repository with one known failing state
and one known passing state, a project, a dossier decision gate, and an epic
represented by two tasks. The full level performs these ordered transitions:

1. authenticate at the protocol boundary and, for Compose, prove the folded
   browser health, browser shell, and grouped-task API checks;
2. register the fake coding Runner;
3. register the project fixture and its two tasks;
4. claim the ready task with a fenced lease;
5. run the fixed fake CLI, observe the failure, make the fixture pass, create
   one Git commit, and capture a fixed log;
6. upload the log, acknowledge the immutable result handoff, and complete the
   coding run;
7. submit a passing, immutable fake-review report and settle the server-owned
   orchestration stages;
8. prove the result commit is the clean integration branch tip;
9. move the task to Completed;
10. persist a user turn and an orchestrator turn with a source receipt;
11. record the dossier decision;
12. create a verified Task Server backup;
13. restore it through Task Server's empty staging database; and
14. compare a canonical inventory hash before and after restore.

`smoke` is exactly the first six steps. `full` is all 14. The scenario uses the
fixed visible clock `2026-09-06T12:00:00Z`, fixed fake CLI and review output,
local Git only, and no network other than the selected target. Polling uses the
bounded target-readiness contract and never sleeps longer than 200 ms.

## Run it

From the repository root:

```bash
scripts/scenario.sh --target inproc --level smoke
scripts/scenario.sh --target inproc --level full
scripts/scenario.sh --target compose --level full
```

`inproc` hosts the real Task Server application in the test process with a
temporary store. It needs .NET 10 and Git, works on Windows through Git Bash and
on Linux, and is the card and promotion smoke gate. It first runs the bounded
happy-path case from `TopologyTests` and stores that harness output as
`topology-harness.log`; the existing Linux process-topology assertions remain
in force while the shared HTTP scenario supplies the portable Windows proof.

`compose` builds the normal `orchestrator-api`, `frontend`, `task-server`, and
`studio-bff` services, then runs the scenario executable as the `scenario-runner`
fake-CLI image. The temporary Compose project and volumes are removed on both
success and failure. This level replaces the old independent Compose curl
smoke. The compatibility script `scripts/compose-smoke-test.sh` now delegates
to `compose smoke`.

For a deployed Task Server or Studio BFF:

```bash
export SCENARIO_BASE_URL=https://studio.example.invalid
export SCENARIO_AUTH_TOKEN='<management-capable token>'
export SCENARIO_RUN_ID=cutover-20260906
scripts/scenario.sh --target remote --level full
```

The remote target creates only IDs carrying its `SCENARIO_RUN_ID`. It never
restores over the remote live store: the restore step asks Task Server to verify
the backup through isolated staging, then inventory equality proves that the
live scenario inventory did not change. Cleanup archives both scenario tasks.
The Task Server API intentionally has no project-delete route, so the isolated,
archived project remains as audit evidence. Use a unique run ID for every
cutover rehearsal.

Set `SCENARIO_RESULTS_DIR` to choose the output directory. If it is unset, the
runner uses `JOB_RESULTS_DIR`, then `results/deployment-scenario` as a local
fallback.

## Read the report

Every invocation has a stable process exit code: `0` means every selected step
passed, `1` means at least one step failed, and `2` means the command line or
required target configuration was invalid. It writes:

- `scenario-report.md`, the deployment-card artifact with a status, duration,
  and relative evidence link for every step;
- `scenario-junit.xml`, the same result as JUnit for CI test presentation; and
- `<step-id>.json`, the typed observations and assertions for each executed
  step.

After one failure, later steps are recorded as `not-run`; the runner does not
hide secondary work behind a green aggregate. CI requires two consecutive
`inproc smoke` passes on both Windows and Linux. The release workflow runs
`compose full` next to **Test release topology** and uploads the report even
when the scenario fails.

## Extend it

A deployment-affecting feature adds its regression proof here, not in a new
ad-hoc deployment script:

1. append one step to `testsupport/scenario/deployment-scenario.json` with a
   stable kebab-case ID, operator-facing title, and at least one typed
   assertion;
2. add the matching action in `testsupport/scenario/Program.cs`; drive the
   public HTTP contract so the same action works in all three targets;
3. emit only deterministic observations and write supporting data through the
   step evidence file;
4. increase the `full` step count, and increase `smoke` only when the new proof
   belongs in the reduced deployment-card gate; and
5. run `inproc full` twice, then `compose full` where Docker is available.

Do not add fixed sleeps, live provider calls, target-external network access,
or a target-specific success criterion. If a target cannot support a required
operation safely, encode the bounded equivalent in the shared action and make
the mode explicit in its evidence, as the remote restore verification does.

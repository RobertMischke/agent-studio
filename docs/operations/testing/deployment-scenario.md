# Deployment regression scenario

The deployment scenario is the shared acceptance contract for deployment cards
and releases. It proves one small lifecycle through the Task Server boundary:
principal attribution, Runner registration, a fenced coding claim, a fixed fake
CLI commit and log, remote-style review evidence, orchestration settlement,
completion, an orchestrator context receipt, a dossier decision, backup,
restore verification, and canonical inventory equality.

The source of truth is
[`testsupport/scenario/deployment-scenario.json`](../../../testsupport/scenario/deployment-scenario.json).
It contains the ordered steps, fixture data, fixed visible clock, typed
assertions, and evidence names. The fixture repository starts with one passing
and one failing Node test. The fake coding CLI makes one deterministic commit;
the fake review CLI proves both tests pass. The scenario does not call the
internet after its target has started and polls readiness at one-second
intervals.

## Run it

From the repository root:

```bash
# Host-native card gate. Runs on Windows (Git Bash) and Linux.
scripts/scenario.sh --target inproc --level smoke

# Release gate. Builds and starts the default and distributed Compose services.
scripts/scenario.sh --target compose --level full

# A deployed control plane or cutover candidate.
scripts/scenario.sh --target remote --level smoke \
  --url https://studio.example.test \
  --token-file /path/to/task-server-token
```

`smoke` is exactly the first six definition steps and has a three-minute
budget. `full` runs all steps. `--repeat 2` requires two consecutive green runs
and retains a separate report for each run. CI applies this to the Linux and
Windows card gate before merge.

`inproc` starts Task Server and Studio BFF as sibling processes owned by the
script, following the same exact-PID lifecycle used by `TopologyTests`. It does
not sweep unrelated processes. `compose` activates the `distributed` and
`runner` profiles, starts the bounded services needed by the scenario, and
includes the former Compose onboarding health, browser-shell, and grouped-task
checks. Its Runner services use `testsupport/scenario/Dockerfile`, which contains
the real Agent Host with a fixed local CLI instead of provider CLIs or provider
credentials. `remote` creates a uniquely named scenario project. Its cleanup
archives every scenario card, including failures. Project deletion is not part
of the current Task Server contract.

For safety, remote full runs perform the restore step as an isolated integrity
verification by default. Set `SCENARIO_REMOTE_ALLOW_RESTORE=1` only on a
dedicated empty cutover target to exercise replacement restore. In-process and
Compose full runs use a fresh store, enter Maintenance, restore the snapshot,
return to Normal, and compare the project inventory hash.

The remote URL may be either Studio BFF or Task Server. Credentials are read
from `--token-file`, not command-line text. The runner never prints the token.

## Reports and exit codes

Reports go to `JOB_RESULTS_DIR` when it is set, otherwise to `./results`. A run
produces:

- `deployment-scenario-<target>-<level>.md`: the stable card-status report;
- `deployment-scenario-<target>-<level>.junit.xml`: CI test results;
- `deployment-scenario-<target>-<level>.json`: machine-readable summary;
- a matching numbered directory with one JSON evidence document per step and
  the fixed fake-CLI logs.

The Markdown table links each step to its evidence and records status and
duration. Evidence includes the typed assertion, fixed scenario timestamp,
observed identifiers, hashes, and any failure stack. A failed step is followed
by explicit skipped rows, so the report is useful even when the stable exit code
is nonzero.

| Exit | Meaning |
|---:|---|
| `0` | Every selected step passed. |
| `1` | A scenario action or typed assertion failed. |
| `2` | Arguments or credentials were invalid. |
| `3` | The selected target could not start or become ready. |

## Add a deployment feature

Add its regression coverage here, not in a new deployment smoke script:

1. Add one ordered step to `deployment-scenario.json`. Choose `smoke` only when
   every deployment card needs the assertion and the three-minute budget holds.
2. Give the step a typed assertion and a stable JSON evidence filename. Add the
   assertion type to the runner allow-list.
3. Add the bounded action to `scripts/scenario-runner.mjs`. Use the existing
   target URL, fixed scenario clock, fake outputs, and request helper. Do not add
   target-specific step order.
4. Run `inproc smoke` twice. For full-only behavior, also run `inproc full` and
   `compose full` on a Docker host.
5. Check the Markdown links and JUnit failure behavior. Preserve the report in
   the card's collected results directory.

`scripts/compose-smoke-test.sh` is a compatibility entry point that delegates
to `compose smoke`; it has no separate scenario or assertions.

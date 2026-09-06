# Deployment regression scenario

The deployment scenario is the shared acceptance run for deployment cards and
releases. It proves one small coding lifecycle against the selected Task Server:
project and fixture creation, fenced claim, deterministic fake coding CLI,
immutable result handoff, deterministic Remote Review, orchestration settlement,
completion, context-receipt persistence, Dossier decision evidence, backup,
restore verification, and logical inventory equality.

The ordered source of truth is
[`testsupport/scenario/deployment-scenario.json`](../../../testsupport/scenario/deployment-scenario.json).
Its shape is pinned by
[`deployment-scenario.schema.json`](../../../testsupport/scenario/deployment-scenario.schema.json)
and checked again by the runner before target startup.
Each step declares typed assertions. The fixture repository starts with one
known failing test; the fake coding CLI changes one file, creates one commit,
and makes the same test pass. Inputs that expose time use the fixed clock in the
definition. The CLIs have fixed output and make no network calls.

## Run targets

Run the first six steps on a local Windows or Linux checkout:

```bash
scripts/scenario.sh --target inproc --level smoke
```

This target builds the Task Server when its Release assembly is absent, starts
it on an ephemeral loopback port with an isolated store, and applies the same
process ownership and readiness conventions as `TopologyTests`. It does not
require Docker. The smoke contract has a three-minute ceiling in CI; a normal
warm run takes seconds.

Run the entire scenario against the Compose deployment:

```bash
scripts/scenario.sh --target compose --level full
```

The driver uses a unique Compose project, the default Studio services, the
`distributed` Task Server profile, and a deterministic fake-CLI image behind
the `runner` profile. It checks the former Compose smoke
conditions (frontend health, application shell, and grouped-task API), then
runs the shared steps against the containerized Task Server. It always captures
Compose logs and removes the scenario volumes. For restore, it copies the
backup out, deletes only the uniquely named scenario data volume, starts an
empty Task Server store, copies the backup back, and verifies inventory
equality after restore. Docker Engine and Compose are
required; on `agent-runner-01`, Docker group membership remains an operator
prerequisite.

Run against a deployed Task Server or Studio BFF URL:

```bash
SCENARIO_BASE_URL=https://studio.example.test \
SCENARIO_TOKEN="<deployment-scenario credential>" \
scripts/scenario.sh --target remote --level full
```

For a legacy role-authenticated server, set `SCENARIO_STUDIO_TOKEN` and
`SCENARIO_RUNNER_TOKEN` instead of the shared `SCENARIO_TOKEN`.

Remote identities receive a unique suffix. Cleanup archives every scenario task
because the Task Server intentionally has no project-deletion API. Remote backup
restore is `verifyOnly` so the check cannot replace a shared deployed store.
Cutover checks that own a disposable empty store should run the Compose target
for the destructive restore proof, then run the remote target for URL, TLS, and
credential proof. Neither target contacts a service outside the selected target
and the local temporary Git fixture.

`smoke` always means the first six ordered steps. `full` means all declared
steps. The runner polls readiness at 100 ms and has no unconditional sleep
longer than that polling interval.

## Reports and exit codes

By default, artifacts are written to
`$JOB_RESULTS_DIR/deployment-scenario/`, or to
`results/deployment-scenario/` when the job variable is absent. Override this
with `--output <directory>`.

- `scenario-report.md` is the deployment-card status artifact. Its table lists
  status, duration, and a relative evidence link for every executed step.
- `scenario.junit.xml` is the CI test report. One test case represents one
  scenario step.
- `evidence/*.json` contains the typed result used by each assertion. Fixed CLI
  logs and target logs are stored beside those files.

Exit code `0` means every selected step passed. `1` means a step or assertion
failed. `2` is command-line usage. `3` means the requested target dependency or
URL was unavailable. The run stops after the first failed ordered step, still
writes both reports, and never converts a failure into a warning.

## Extend the regression suite

When a deployment feature adds a lifecycle guarantee, extend this scenario
instead of adding an ad-hoc deployment check:

1. Add one ordered object to `deployment-scenario.json`. Give it a stable id,
   include it in `full`, and add it to `smoke` only when it belongs in the
   reduced six-step contract.
2. Declare at least one assertion using an existing typed assertion. Add a new
   assertion type to `validate_definition` and `Scenario.assert_step` only when
   no existing type expresses the contract.
3. Add the target-neutral action as `Scenario.step_<id>` in
   `scripts/scenario.py`. Keep target differences in target setup or in an
   explicit expected-by-target value in the definition.
4. Emit small JSON evidence and add a focused contract test in
   `scripts/scenario_test.py`. Do not add retries for product failures.
5. Run the smoke level twice consecutively and the affected full target once.
   Attach `scenario-report.md` to the card status.

The pull-request workflow runs two consecutive smoke passes on both Linux and
Windows. Release CI runs `compose full` next to `Test release topology`; either
gate blocks publication when a scenario step fails.

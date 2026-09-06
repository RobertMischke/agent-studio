# Deployment regression scenario

The deployment regression scenario is the shared release proof for Agent
Studio's distributed topology. It replaces deployment-specific curl lists with
one versioned, seeded lifecycle. A deployment card runs the smoke level. A
release runs the full level against the images built from the release SHA.

## What it proves

The scenario starts from a tiny Git repository whose test is deliberately red.
It creates one project, a decision dossier, an Epic with two child tasks, and a
separate Ready coding task. Fixed fake CLIs make the test green, commit the
change, and review the immutable result. The full level then proves integration,
completion, an orchestrator transcript with a context receipt, a dossier
decision, backup integrity, restore, and normalized inventory equality.

The smoke level is exactly the first six ordered steps: bootstrap, runner
registration, task creation, fenced claim, coding result handoff, and remote
review. It is expected to finish in less than three minutes. The definition is
[`testsupport/scenario/definition.json`](../../../testsupport/scenario/definition.json).
Every assertion in that file has an explicit type. Unknown types fail before
the target is mutated.

## Run it

From a source checkout with .NET 10, Git, Node.js, and a POSIX shell:

```bash
scripts/scenario.sh --target inproc --level smoke
scripts/scenario.sh --target inproc --level full
```

`inproc` uses `TaskServerStore` directly with the fixed scenario clock. It runs
on Windows through Git Bash and on Linux and needs no Docker or network.

On a Docker Engine with Compose:

```bash
scripts/scenario.sh --target compose --level full
```

The wrapper enables the `distributed` and `runner` profiles, builds the current
Task Server, Studio BFF, and deterministic scenario-runner image, and runs the
same definition over the Compose network. It owns a named Compose project and
removes its containers and volumes on exit. `scripts/compose-smoke-test.sh` is a
compatibility alias for `compose smoke`.

To verify an already deployed Task Server:

```bash
SCENARIO_AUTH_TOKEN='<token>' scripts/scenario.sh \
  --target remote \
  --level full \
  --url https://task-server.example.test
```

The remote target uses a uniquely named scenario project. It verifies the
backup without restoring shared data, compares the inventory before and after,
and archives its scenario tasks after the final assertion. The credential must
be allowed to use management, runner, review, and orchestration routes. No
request leaves the supplied target origin.

Use `--output <directory>` to select the artifact root. In managed task runs the
wrapper defaults to `JOB_RESULTS_DIR`. Otherwise it writes to
`scenario-results/` in the checkout.

## Report contract

Each target and level writes a stable directory named
`deployment-scenario-<target>-<level>` with:

- `scenario-junit.xml`, one test case per executed step and a failing process
  exit code when any case fails;
- `scenario-report.md`, a human-readable table with status, duration, and a
  relative evidence link for every step;
- `evidence/<step-id>/`, containing the relevant IDs, hashes, logs, and typed
  response snapshots.

Exit code `0` means every selected step passed. Exit code `1` means a scenario
step or harness boundary failed. Exit code `2` means invocation or definition
validation failed. CI uploads the directory even when the scenario fails, so a
deployment status can link the Markdown report without reconstructing logs.

## Determinism

The definition pins submitted timestamps, the in-process clock, fixture
content, fake-CLI output, and step order. Server-owned timestamps on HTTP
targets remain authoritative and are excluded from equality checks. The run
performs no external network request. Polling uses a 100 ms interval with a 30
second deadline and has no unconditional sleeps. IDs that must be unique on a
shared remote target are excluded from the normalized inventory hash. The
flake budget is zero. This scenario card requires two consecutive green smoke
executions before merge; later cards need one green smoke execution through
their normal card gate.

## Add a feature step

Deployment-visible features extend this scenario instead of adding a separate
smoke script:

1. Add one ordered step to `definition.json`. Keep smoke at six steps unless the
   release-critical minimum itself changed.
2. Declare each expected result with an existing typed assertion. Add a new
   assertion type to definition validation only when none of the current types
   describes the contract.
3. Add the bounded operation to both the in-process and HTTP adapters. Remote
   behavior must remain scoped to the uniquely named scenario project and must
   have an explicit cleanup or verify-only boundary.
4. Write evidence below that step's directory and keep credentials and raw
   secrets out of every report.
5. Run smoke twice consecutively, then run full. If the feature changes a
   container boundary, also run `compose full`.

Do not add sleeps to handle eventual consistency. Use the definition's bounded
polling contract and make timeout failures typed and visible.

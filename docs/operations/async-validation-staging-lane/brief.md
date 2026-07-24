# Async Validation & the Test Staging Lane — brief

A design proposal responding to the intuition "tests are slow, run them
asynchronously so they don't block the shared runner." It rests on the
[pipeline-time-economy](../../quality/pipeline-time-economy/) evidence workbench
and does not re-derive the numbers.

## The proposal in three building blocks

1. **Deterministic test scoping** — run only new tests and tests touching the
   changed module, via a git-diff→test-project mapping (0 tokens). The LLM
   variant is rejected: it spends the expensive resource (tokens) to save the
   cheapest one (CPU).
2. **A staging lane** — "done, not yet integrated" cards collect and move on
   batched, once their changes pass the gate together.
3. **A test-integration branch** — batched validation on `integration/test`,
   then fast-forward to `develop` only when green.

## Decision

- **Reject** LLM-based test selection (anti-economical).
- **Pays off regardless:** cache the gate result per tested tree SHA; skip
  unchanged scopes on a reissue. Removes most of the redundant re-testing.
- **Measure first:** the staging lane + test branch only pay off if the batch
  green rate is high; a red batch forces bisection that can eat the gain.

## Why it is throughput, not cost

Per the evidence workbench: test CPU is ~0.05% of token spend (~2000×), so this
is not a money optimization. The lever is the machine-wide gate lock
(`BuildTestGateRunner.MachineGateLockPath`) that serializes the shared runner,
and the fact that ~84% of test-gate time is retries. Closely related to the gate
serialization work in [haertung-verteilte-ausfuehrung](../haertung-verteilte-ausfuehrung/) §5.

## Related

- Evidence: [pipeline-time-economy](../../quality/pipeline-time-economy/)
- [token-economy-task-data](../../quality/token-economy-task-data/) — AGT-2293
- QS-28 skip-if-fresh (deterministic skip via hashes; same idea for review units)

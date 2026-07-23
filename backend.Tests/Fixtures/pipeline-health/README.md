# Pipeline health night replay fixture

`night-2026-07-22-23.normalized.jsonl` is the reduced, normalized replay slice
for the operator incident of 22/23 July 2026.

The gate identity and 06:37:37 CEST acquisition time come directly from the
backend log archaeology. The cross-card failure rows and lane inventory retain
the observed ordering and counts while replacing task and repository identities
with inert fixture values. Timestamps are UTC. `replay_end` is the 09:15 CEST
backend restart that finally released the wedged lock.

The fixture deliberately contains no completion for gate `7bbed536`. The replay
must therefore produce:

- the systemic fingerprint alarm on the third distinct card;
- the filled `4-auto-review` zero-drain alarm from the inventory snapshot;
- the hanging-gate alarm 30 minutes after acquisition, well before restart.

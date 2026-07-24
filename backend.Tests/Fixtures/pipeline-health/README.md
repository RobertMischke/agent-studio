# Pipeline health night replay fixture

`night-2026-07-22-23.normalized.jsonl` is the reduced, normalized runtime-event
replay slice for the operator incident of 22/23 July 2026. Every row follows
the `ProductRuntimeEvent` envelope, while the incident-specific dimensions
live under `payload`.

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

The deterministic alarm schedule is:

| Signal | First observable input | Alarm time | Detection latency |
| --- | --- | --- | --- |
| Filled `4-auto-review` at `0/h` | 04:20:00Z inventory | 04:20:00Z | Immediate on the first sensor evaluation |
| Repeated fingerprint across three distinct cards and two projects | 04:21:12Z first failure | 04:29:31Z third failure | 8 min 19 s |
| Acquired gate without completion | 04:37:37Z acquisition | 05:07:37Z budget expiry | 30 min |

All three alarms predate the 07:15:00Z restart. The replay test asserts the
exact fingerprint latency, the cross-project sequence, the code-owned gate
budget, and that every alarm time falls inside the incident window.

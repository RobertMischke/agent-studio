# Demo Replay Trace Contract

Version: `agent-studio/demo-replay-trace/v1`

Status: the replay plane of the public demo instance (dossier
[AGT-W34](../../operations/demo-instanz/index.html), slice S3). This contract
covers the signed fixed trace, the narrow server scope that ingests it, and the
credential rule that keeps the replay identity out of execution. It is not a
general runner protocol and must not be used to drive real work.

## Why the trace carries the content

The dossier launch invariant refuses to treat a flag or a mode as an execution
boundary. Applied to replay, that means the replay service must not be trusted
with what a visitor sees. So the trace, not the request, carries the content:

- The Task Server and the replay service both hold the same signed trace file
  from the release bundle.
- A replay request carries only a cursor: trace id, trace digest, epoch, and
  sequence.
- The server materializes the event body from its own verified copy.

A fully compromised replay host can therefore advance a script a reviewer
already read. It cannot author prose, invent token figures, or name a task of
its choosing, because there is no field in which to say those things.

## Trace file

```jsonc
{
  "schemaVersion": 1,
  "traceId": "demo-instanz-cycle-1",
  "cycleSeconds": 720,
  "scene": { "projects": ["Demo App"], "taskKeys": ["DEMO-4", "DEMO-12"] },
  "events": [
    {
      "sequence": 1, "offsetMs": 0, "taskKey": "DEMO-4",
      "kind": "session.started", "severity": "info",
      "message": "Simulated run started for large avatar uploads.",
      "durationMs": null, "inputTokens": null, "outputTokens": null
    }
  ],
  "signature": {
    "algorithm": "hmac-sha256", "keyId": "demo-replay-dev",
    "digest": "<sha256 of the canonical form>",
    "value": "<hmac-sha256 of the canonical form>"
  }
}
```

Committed instance: [`testdata/demo-replay/demo-replay-trace.json`](../../../testdata/demo-replay/demo-replay-trace.json).
Generator: [`scripts/demo-replay/generate-trace.mjs`](../../../scripts/demo-replay/generate-trace.mjs).
C# records and canonicalizer: `contracts/TaskServer.Contracts/DemoReplayContracts.cs`.

## Canonical form and digest

The digest is taken over a deterministic line projection, not over JSON, so it
cannot drift on property order, whitespace, or number formatting between the
signing tool and the verifier. Messages enter the form as their own SHA-256,
which removes every escaping question.

```
agent-studio/demo-replay-trace/v1
schemaVersion=<int>
traceId=<id>
cycleSeconds=<int>
scene.project=<name>            (once per project, file order)
scene.taskKey=<key>             (once per key, file order)
event=<sequence>|<offsetMs>|<taskKey>|<kind>|<severity>|<durationMs>|<inputTokens>|<outputTokens>|<sha256(message)>
```

Lines are joined with `\n` and the form ends with a trailing `\n`. Absent
numbers and an absent severity render as the empty string. `digest` is the
lowercase hex SHA-256 of the UTF-8 bytes; `value` is the lowercase hex
HMAC-SHA256 over the same bytes.

`digest` is what the S5 release manifest pins. `value` is what proves the trace
came from the bundle build. The replay service holds no signing key: it forwards
a signature it cannot produce.

## Verification rules

`DemoReplayTraceVerification.Verify` is pure and shared by both sides. The Task
Server runs it at startup and refuses to start the plane on any rejection; the
replay service runs it at boot so a corrupted bundle fails on the replay host
instead of becoming rejected traffic.

| Rejection | Rule |
|---|---|
| `trace-schema-unsupported` | `schemaVersion` is not 1 |
| `trace-id-invalid` | id is not 3 to 64 lowercase letters, digits, or hyphens |
| `trace-cycle-out-of-range` | `cycleSeconds` outside 60 to 3600 |
| `trace-scene-empty` | no projects or no task keys |
| `trace-scene-namespace-denied` | a scene key is outside the `DEMO-` / `PLAT-` namespace of ADR-0056 |
| `trace-event-count-out-of-range` | zero events, or more than 2000 |
| `trace-sequence-not-dense` | sequences are not exactly 1..N in order |
| `trace-offset-not-monotonic` | an offset goes backwards |
| `trace-offset-outside-cycle` | an offset is negative or at or past the cycle length |
| `trace-scene-key-out-of-scope` | an event names a task the scene does not declare |
| `trace-event-kind-denied` | kind is not one of `session.started`, `turn.started`, `turn.completed`, `session.completed` |
| `trace-message-not-printable` | message carries a control character or exceeds 500 characters |
| `trace-signature-missing` | no signature, key id, or value |
| `trace-signature-algorithm-denied` | algorithm is not `hmac-sha256` |
| `trace-digest-mismatch` | recomputed digest differs from the signature or from the pinned manifest digest |
| `trace-signature-invalid` | HMAC does not match the configured signing key |

Digest and signature comparisons use a fixed-time equality check.

## Ingestion scope

`POST /api/demo-replay/events` is the entire mutation surface of the plane, and
`GET /api/demo-replay/state` is its read-only cycle projection. Both routes are
mapped only when a verified trace is configured at startup, so a deployment
without one answers 404 rather than exposing a reachable handler.

Request body: `{ "traceId", "traceDigest", "epoch", "sequence" }`.

`DemoReplayAdmissionPolicy` is pure and decides one step at a time:

| Denial | Rule |
|---|---|
| `replay-disabled` | the plane is not enabled |
| `replay-trace-unknown` | the request names another trace |
| `replay-scene-task-missing` | the scene task the step names is absent from the datastore |
| `replay-digest-mismatch` | the request digest is not the pinned digest |
| `replay-epoch-stale` | the epoch is behind the server |
| `replay-epoch-skipped` | the epoch is more than one ahead |
| `replay-epoch-too-soon` | a later epoch opened before the minimum interval, default half a cycle |
| `replay-sequence-out-of-order` | not exactly the next step, or a new epoch that does not restart at 1 |
| `replay-sequence-out-of-range` | outside 1..event count |
| `replay-rate-limited` | the rolling 60 second budget is spent |

A refusal answers 409 and reports the authoritative `epoch` and `lastSequence`,
so a restarted replay service resynchronizes instead of retrying a step the
server will never accept.

An admitted step appends exactly one `RunnerRecordedEvent` with
`simulated: true` to the scene task's `logs/runner-events.jsonl`, through the
same `RunnerEventJournal` a real runner uses. Replay writes nothing else: no
task prompt, no decision, no lane transition, no Git state.

## Credential rule

`demo.replay` is a Runner scope so it reuses the fail-closed credential store,
but it is deliberately outside `RunnerScopes.Minimum`:

- A default enrollment never receives it.
- `RunnerScopeCompositionPolicy` rejects any credential that holds `demo.replay`
  together with any other scope, at issuance and at rotation. A credential that
  can replay can never claim.
- `AccessSecurityMiddleware` maps the replay route to `demo.replay` and nothing
  else. A replay credential reaching any other `/api/` route is refused, and an
  ordinary coding credential reaching the replay route is refused.

`backend.Tests/DemoReplayCompromiseTests.cs` runs the full matrix against the
real networked pipeline.

## Service boundary

The replay service is `demo-replay/DemoReplayRunner.csproj`, a separate program
rather than a mode of the agent host, because a mode would keep the coding CLI,
Git, worktree, and provider-credential code inside the shipped image. Its only
reference is the wire contract, and its runtime image is the .NET runtime image
with nothing installed on top.

| Property | How it is held |
|---|---|
| No CLI, Node, Git, or ssh client | runtime base image, no package installs |
| No repository or worktree | no Git code and no workspace configuration exists |
| No secret mount | the exclusive replay credential arrives as an environment variable |
| Narrow egress | one route on one host; plain http to a non-loopback host is refused |
| Unprivileged | runs as `$APP_UID`, trace baked read-only into the image |

`demo-replay.Tests/DemoReplayImageGuardTests.cs` asserts each of these against
the Dockerfile, the project file, and the service source.

Environment: `DEMO_REPLAY_SERVER_URL`, `DEMO_REPLAY_TRACE_PATH`,
`DEMO_REPLAY_AUTH_TOKEN`, `DEMO_REPLAY_RUNNER_NAME`,
`DEMO_REPLAY_ALLOW_INSECURE_HTTP`, `DEMO_REPLAY_TICK_SECONDS`,
`DEMO_REPLAY_REQUEST_TIMEOUT_SECONDS`. Exit codes: 0 stopped on signal,
2 configuration or trace rejected.

## Server configuration

```jsonc
"DemoReplay": {
  "Enabled": "true",
  "TracePath": "/opt/agent-studio/demo-replay-trace.json",
  "TraceDigest": "<sha256 pinned by the release manifest>",
  "SigningKeyFile": "/etc/agent-studio/demo-replay.key",
  "MinEpochSeconds": 360,
  "MaxEventsPerWindow": 8
}
```

`Enabled` is read once at startup. There is no project setting, management
command, or browser toggle that turns the plane on, off, or wider at runtime.
`TraceDigest` and the signing key are optional for local verification; a public
deployment sets both.

## Labeling

Every row the plane writes carries `simulated: true`. It reaches the frontend
two ways: as a first-class field on `RunnerRecordedEvent`, and as
`metadata.simulated` on the projected conversation event. The recorded-replay
panel labels both the section and each affected row Simulated, using the teal
provenance pigment rather than an acute status colour.

## Non-goals

- Replay is not a runner mode, and the replay identity is not a runner identity.
- The plane does not move lanes, write prompts, record decisions, or touch Git.
- It carries no benchmark, cost, or model-comparison meaning. Figures inside a
  trace are scene dressing that a reviewer wrote, not measurements.
- It is not the public visitor boundary. The read-only edge, CSP, rate limits,
  and project-filtered SignalR belong to slice S4.

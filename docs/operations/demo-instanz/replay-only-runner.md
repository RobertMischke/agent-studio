# Replay-only Runner for the public demo instance

Slice S3 of dossier [AGT-W34](index.html). It gives the public demo a living
execution plane without giving it execution authority: a separate service
replays a signed fixed trace through one narrow server scope, and every event it
produces is labelled Simulated.

This slice is one of the two independent barriers the dossier's recommended
topology asks for. The other is the hard server execution lock (slice S2). They
do not depend on each other at runtime: even with S2 absent, a replay credential
cannot claim, lease, or mutate a task.

## What the replay may do

Exactly one thing: append simulated runner lifecycle events to a task inside the
pinned demo scene. It cannot write prompts, decisions, files, Git state, lane
transitions, leases, artifacts, or completions, because there is no route that
would let it.

The admission decision is a pure policy,
`backend/Features/DemoReplay/DemoReplayAdmissionPolicy.cs`. It admits a frame
only when all of the following hold, and returns a typed denial otherwise.

| Condition | Denial code |
|---|---|
| The instance is configured with a pinned digest and a verification key | `replay-disabled` |
| Trace, digest, task key, kind, epoch, and sequence are present and positive | `replay-request-invalid` |
| The frame declares the pinned trace id | `replay-trace-mismatch` |
| The frame declares the pinned trace digest | `replay-trace-digest-mismatch` |
| The task key is inside the seeded `DEMO-*` / `PLAT-*` namespace | `replay-task-not-in-scene` |
| The kind is a lifecycle or diagnostic event, never a lane or decision event | `replay-kind-not-simulatable` |
| The frame seal verifies against the pinned public key | `replay-signature-invalid` |
| The epoch is the current one or newer | `replay-epoch-stale` |
| The sequence increases strictly inside that epoch | `replay-sequence-not-monotonic` |

Every admitted event is written with `origin: simulated`. The origin is assigned
by the server, not by the caller: neither ingest route accepts an origin field,
so a live runner cannot claim to be simulated and a replay cannot claim to be
live.

## Why the trace is signed per frame

The private signing key belongs to the release bundle build and never reaches
the demo VM, because the image is required to carry no secrets. So the trace
ships with one detached signature per frame. The replay service holds only
pre-signed material: an attacker who owns the replay process can re-emit the
recorded scene, but cannot mint a frame the server would accept.

The epoch and sequence are deliberately outside the signature. Content
authenticity comes from the seal; anti-replay comes from the server-side cursor.
Residual risk: a compromised replay process can advance the epoch and re-play
the same recorded scene. That changes nothing a visitor sees beyond timing, and
it remains inside the scene the release pinned.

## Sealing a trace

```bash
# Once per release key. Keep the private key in the bundle build.
node scripts/demo-replay/seal-trace.mjs keygen --out-dir ./secrets

# Once per trace revision.
node scripts/demo-replay/seal-trace.mjs seal \
  --trace scripts/demo-replay/demo-scene.trace.json \
  --private-key ./secrets/demo-replay-signing.pem \
  --key-id demo-release-2026-08 \
  --out ./dist/replay-trace.json
```

The command prints the digest. Pin that value as `DemoReplay:TraceDigest` on the
server. The authored scene must stay inside the printed ASCII subset: the
sealing tool and the .NET verifier have to produce byte-identical canonical JSON,
and the tool fails loudly rather than silently disagreeing.

## Server configuration

Startup-only. There is no management route and no browser toggle.

| Key | Meaning |
|---|---|
| `DemoReplay:Enabled` | Off by default. The scope stays closed unless this is `true`. |
| `DemoReplay:TraceId` | The pinned trace id. |
| `DemoReplay:TraceDigest` | The pinned canonical digest printed by the sealing tool. |
| `DemoReplay:SigningKeyId` | Release key identifier, recorded for the bundle manifest. |
| `DemoReplay:PublicKeyBase64` | Base64 SubjectPublicKeyInfo of the release public key. |
| `DemoReplay:TaskKeyPrefixes` | Optional. Defaults to the ADR-0056 `DEMO-` and `PLAT-` namespaces. |

The credential is separate. Mint it as an owner in the networked profile:

```
POST /api/auth/runner-enrollments   { "name": "demo-runner-replay", "scopes": ["demo.replay"] }
POST /api/auth/runner-enroll        { "code": "<enrollment code>" }
```

`demo.replay` is structurally exclusive. A credential that carries it may carry
no other Runner scope, and the store rejects the combination with `invalid-scope`
rather than trusting an operator to get it right.

Note the profile boundary: credential scopes are enforced by the networked
security profile, which is what the public demo runs. A local loopback instance
has no authentication at all by design, so there the trace pin, the seal, the
epoch, and the scene are what constrain replay.

## Service configuration

| Variable | Meaning |
|---|---|
| `DEMO_REPLAY_SERVER_URL` | The one origin this service may reach. |
| `DEMO_REPLAY_TRACE_FILE` | Signed trace, mounted read-only. |
| `DEMO_REPLAY_PUBLIC_KEY_FILE` | Verification key, so a swapped bundle fails at boot. |
| `DEMO_REPLAY_AUTH_TOKEN_FILE` | The replay credential. Never accepted on the command line. |
| `DEMO_REPLAY_EPOCH` | First epoch. Each cycle increments it. |
| `DEMO_REPLAY_SPEED` | Playback speed factor. `1.0` runs the authored twelve-minute scene. |
| `DEMO_REPLAY_CYCLE_PAUSE_SECONDS` | Pause between cycles. |

The image is built from `demo-replay-runner/Dockerfile` on the plain .NET runtime
base. It contains no coding CLI, no Node.js, no Git, no repository, no Docker
socket, and no credential, and it runs as an unprivileged account. Egress is
default-deny on the demo VM; the process enforces the same allowlist internally
through `ReplayEgressLock`, which permits one origin and two paths and throws
before a socket is opened for anything else.

## Local verification

```bash
node scripts/demo-replay/seal-trace.mjs keygen --out-dir /tmp/demo-keys
node scripts/demo-replay/seal-trace.mjs seal \
  --trace scripts/demo-replay/demo-scene.trace.json \
  --private-key /tmp/demo-keys/demo-replay-signing.pem \
  --key-id demo-release-2026-08 --out /tmp/demo-keys/replay-trace.json

export DemoReplay__Enabled=true
export DemoReplay__TraceId=demo-scene-reports-export-v1
export DemoReplay__TraceDigest=<digest printed above>
export DemoReplay__PublicKeyBase64="$(cat /tmp/demo-keys/demo-replay-public.b64)"
bash scripts/worktree-test-stack.sh up --demo
eval "$(bash scripts/worktree-test-stack.sh env)"

DEMO_REPLAY_SERVER_URL="$BACKEND_URL" \
DEMO_REPLAY_TRACE_FILE=/tmp/demo-keys/replay-trace.json \
DEMO_REPLAY_PUBLIC_KEY_FILE=/tmp/demo-keys/demo-replay-public.b64 \
DEMO_REPLAY_SPEED=200 \
dotnet run --project demo-replay-runner/DemoReplayRunner.csproj -- --once

bash scripts/worktree-test-stack.sh down
```

Running the same epoch twice is the quickest confidence check: every frame comes
back `replay-sequence-not-monotonic`.

## Where the compromise proofs live

- `backend.Tests/DemoReplayScopeCompromiseTests.cs` drives a real server with a
  real replay credential and asserts that claim, lease, logs, events, artifacts,
  completion, start, continue, stop, move, and batch-move are all refused, that
  the board cannot even be read, and that a tampered or foreign-signed frame is
  rejected while a sealed one lands as `origin: simulated`.
- `backend.Tests/DemoReplayAdmissionPolicyTests.cs` pins the admission matrix.
- `demo-replay-runner.Tests/` pins the egress allowlist, the claim-free service
  surface, trace verification, and cycle planning.
- `frontend/e2e/task-detail/runner-replay-simulated-badge.spec.ts` proves the
  Simulated marker renders in both themes.

## Not in this slice

The hard server execution profile (S2), the public read-only edge (S4), the
scrub and bundle contract (S5), and the dedicated-VM launch rehearsal (S6). This
slice adds no hosting, DNS, or infrastructure change.

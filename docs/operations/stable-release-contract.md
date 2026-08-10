# Stable Release And Update Contract

Stable deploys releases, not folders or moving branches. A deployable Agent
Studio build is identified by an immutable `v<semver>` tag and a
`build-manifest.json` conforming to
[`build-manifest.schema.json`](../schemas/build-manifest.schema.json). Folder
timestamps are never part of freshness, ordering, comparison, or rollback.

## Release identity

The manifest records Agent Studio tag, version, commit, dirty flag, build time,
and integrity. It also records the exact version, tag, commit, and package
integrity for CodingAgentRunner (CAR) and Coding Agent Chat (CAC). A release
build is rejected when any tag is missing, a tag does not equal `v<version>`,
the checkout is dirty, an integrity value is absent, or CAC still resolves from
a local `file:.../dist` dependency.

Generate the manifest only after all three upstream identities are known:

```sh
node scripts/release/generate-build-manifest.mjs \
  --tag=v1.0.0 --version=1.0.0 \
  --car-version=0.5.0 --car-tag=v0.5.0 --car-commit=<sha> --car-integrity=sha512-<value> \
  --cac-version=<version> --cac-tag=v<version> --cac-commit=<sha> --cac-integrity=sha512-<value>
```

The generator uses create-new semantics and refuses to overwrite an existing
manifest. It derives CAR and CAC versions and integrity from
`backend/packages.lock.json` and `frontend/package-lock.json`, then refuses any
supplied metadata that differs. Stable restores with `dotnet restore
--locked-mode` and `npm ci`, so deployment never rewrites a lockfile. The
backend copies the manifest beside the published assembly and exposes the
same identity from `GET /api/system/about` and `GET /api/system/version`.
`GET /healthz` remains body-compatible and adds tag and commit response headers.

## Stable preflight

The update preflight compares four explicit identities:

- running: the backend about response;
- installed: the manifest belonging to the rollback target;
- candidate: the immutable manifest attached to the requested release tag;
- latest approved: the release channel's signed or operator-approved tag.

The result is `same version`, `upgrade`, `downgrade`, `divergence`, or
`comparison unavailable`. Offline comparison is permitted only with a cached
latest-approved tag and cached immutable manifests. A downgrade needs an
explicit rollback or downgrade authorization. Same-version/different-artifact,
running/installed divergence, package mismatch, dirty builds, and missing tags
are hard failures.

The outer updater downloads the candidate release asset to the configured
candidate-manifest cache using create/replace-by-tag semantics. The Update
Service never manufactures it from a branch checkout. It fetches the manifest's
exact tag without overwriting an existing tag, verifies the dereferenced tag,
then reads CAR and CAC pins directly from that commit's lockfiles. Manifest
versions and integrity must match those pins, and CAC must be an exact registry
artifact rather than `file:.../dist`. Only then may Stable check out the commit
detached and install the candidate manifest. It does not use branch distance to
decide whether a release is available. That avoids moving branch identity, a
stale manifest that merely agrees with itself, and a self-referential manifest
commit.

Before mutation, copy the installed manifest and create a self-contained Git
bundle for the installed commit in the run folder. Together they are the exact
rollback target even if the source ref later disappears. Rollback restores the
manifest, can recover the commit from the bundle, and must pass the same runtime
identity comparison after restart. Health alone is not success. Append the full
intended and observed identities, direction, and rollback outcome to deployment
history.

Frontend installation has one additional cache boundary. Agent Studio's
postinstall bridges patch `node_modules/coding-agent-chat` in place, while the
Angular/Vite optimizer cache key does not reflect those changed bytes. Every
Stable update that runs `npm install` must therefore remove
`frontend/.angular/cache` after the install and before frontend startup. After
startup, load the frontend once through `playwright-core` with a `pageerror`
listener registered before navigation. A page error is a failed deployment,
even when the frontend port and backend health endpoint are reachable.

Offline mode is an explicit updater input (`ReleaseMetadataOffline`), not an
inference from cache presence or age. It is accepted only when both cached
manifests and the cached latest-approved tag still pass the same comparison.

## Migration

An installation without a manifest reports `tag=untagged`, `dirty=true`,
`legacy=true`, and no inferred build time. This is an honest migration identity,
not a releasable candidate. Record its current commit as the initial rollback
anchor, then deploy the first tagged Agent Studio release through the normal
preflight.

CAC `0.3.2` is the immutable replay release consumed by Agent Studio. It is
exact-pinned from the npm registry with package commit
`e1183176aa55964986181b894180983793c4f055` and integrity
`sha512-O4pH+zJdIaTNP7FcNwqSMQcX+y05C9tEkBh9AkiguvxHVTPMFWqf/fR7GpTHNgh7wgclrxD3BnkoWad3Gn7aAw==`.
Do not copy a local CAC `dist` into a release or relax that pin to a range.

A task worktree does not update Stable directly. After the integration commit
is accepted, the release owner creates the immutable Agent Studio tag,
generates the build manifest with the CAC identity above, deploys it through
the normal preflight, and records the observed Agent Studio tag, commit, and
CAC version in deployment history. The reissued integration task retains the
original AGT-2170 relationship for audit continuity.

## Three-component topology gate

A release that claims the distributed Agent Studio architecture must pass the
real-process topology gate after the normal solution build:

```bash
dotnet test runner.Tests/AgentRunner.Tests.csproj \
  --no-build \
  --filter "FullyQualifiedName~AgentRunner.Tests.LogShipperCapTests|FullyQualifiedName~AgentRunner.Tests.BoundedOutputBufferTests"
dotnet test task-server.Tests/TaskServer.Tests.csproj \
  --no-build \
  --filter "FullyQualifiedName~TaskServer.Tests.TopologyTests|FullyQualifiedName~TaskServer.Tests.ProtocolTests" \
  --logger "console;verbosity=normal"
```

The gate is bounded and CI-runnable. It must pass all four topology scenarios:
client-off completion and replay, brief Runner transport interruption with
idempotent typed replay, fail-closed Task Server outage with
positive-no-overlap recovery, and authenticated HTTPS event ingestion and
replay. It also runs the published mixed-version fixtures and proves an
unsupported Runner is rejected before registration or claim.

Do not replace a failed scenario with a timeout-only assertion or a manual
observation. The canonical history must contain typed lifecycle evidence for
messages, bounded traces, artifacts, completion, failure classification, and
recovery proof. Review and reissue remain the deployed backend's authority.
Process-parent assertions must show that Studio and its optional BFF own neither
Task Server nor Runner.

The scenario-to-contract map and replay route are maintained in
[Distributed Agent Studio target architecture](../concepts/distributed-agent-studio-target-architecture.md#release-proof).

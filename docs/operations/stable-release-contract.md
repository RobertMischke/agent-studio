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
manifest. The backend copies it beside the published assembly and exposes the
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
Service never manufactures it from a branch checkout. That avoids both moving
branch identity and a self-referential manifest commit.

Before mutation, copy the installed manifest and immutable release artifact to
the run folder as the rollback target. After restart, compare the complete
running about response with the intended candidate manifest. Health alone is
not success. Append the intended identity, observed identity, direction, and
rollback outcome to deployment history.

## Migration

An installation without a manifest reports `tag=untagged`, `dirty=true`,
`legacy=true`, and no inferred build time. This is an honest migration identity,
not a releasable candidate. Record its current commit as the initial rollback
anchor, then deploy the first tagged Agent Studio release through the normal
preflight.

The current CAC `dist` must not be copied into that release. This work depends
on the CAC versioned-release ticket publishing an immutable package plus tag,
commit, and integrity. The task relationship must also mark AGT-2170 as
`relatedTo`. Until the board API and CAC artifact are available, those two
relationship/artifact steps remain explicit release blockers.

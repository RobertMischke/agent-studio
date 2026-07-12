# Stable release identity contract

Stable deployment identity is an immutable Agent Studio release tag plus the
exact artifacts recorded in `release-manifest.json`. Commit distance remains a
useful development signal, but it is not a release or freshness decision.
Folder and assembly timestamps are never identity inputs.

## Build contract

Set `ATPStableBuild=true` for a Stable build. The backend build invokes
`scripts/generate-release-manifest.mjs`, which requires:

- `HEAD` has an exact `vMAJOR.MINOR.PATCH` tag and the checkout is clean;
- CodingAgentRunner is version-pinned in the backend project;
- Coding Agent Chat is a registry version with lockfile integrity, not a local
  `dist` dependency.

The manifest records the app tag, version, full commit, dirty flag, UTC build
time, and each package's version, tag or commit, source, and integrity. The
schema is [release-manifest.schema.json](../schemas/release-manifest.schema.json).
The Coding Agent Chat registry release is an external prerequisite. AGT-2172
depends on that release ticket and is related to AGT-2170; the generator fails
closed while the host still consumes an unversioned local CAC `dist`.

## Update and rollback contract

The update preflight compares running, installed, candidate, and latest
approved tags without network access once those inputs have been captured. It
classifies `upgrade`, `same`, `downgrade`, `diverged`, or `unknown` and rejects
dirty builds, missing/invalid tags, tag/version disagreement, missing package
integrity, and a reused tag whose commit or packages differ. Downgrade requires
the explicit rollback path.

Before restart, the updater writes `release-preflight.json` into the existing
run folder. After restart it reads `/api/system/version` and refuses success
unless the running manifest exactly matches the intended manifest. Update
history and the retained pre-snapshot preserve the rollback target.

## Migration

An existing process without a manifest reports `identitySource` as
`legacy-untagged`; it does not synthesize a release or build time from file
metadata. Its first valid candidate manifest is classified as migration to an
upgrade. A candidate with no manifest stays on the compatibility path only
until the first manifest-backed release is available; it cannot claim tagged
identity.

`GET /api/system/version` and `GET /api/system/about` expose the running
identity. Update Center shows the release tag and the two package versions.

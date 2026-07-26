# Release, installation, update, and rollback

Agent Studio releases are tag-bound, create-once GitHub Releases created only
from an exact `vX.Y.Z` tag whose version matches the repository `VERSION` file.
The release workflow tests the tagged revision, builds self-contained binaries,
creates three archives, writes `SHA256SUMS`, attests the archives, and creates
the GitHub Release. A target host never builds from a checkout.

## Release assets

| Asset | Contents | Supported runtime |
|---|---|---|
| `agent-orchestrator-X.Y.Z-linux-x64.tar.gz` | Task Server, Orchestrator Engine, systemd units, configuration templates, Caddy template, and lifecycle scripts | `linux-x64`, first-class |
| `agent-host-X.Y.Z.tar.gz` | Separate self-contained `agent-host` binaries, runner configuration template, and Linux systemd/resource-policy material | `linux-x64`, first-class; `osx-arm64`, supported |
| `agent-studio-X.Y.Z.tar.gz` | Compiled static Angular application | Any static web host |

The `agent-host` archive deliberately carries both runtime-specific binaries so
the product still has exactly three distributables. Operators select the
directory matching the host runtime. The macOS archive content has no systemd
claim. A launchd integration remains host-owned until a product launchd unit is
shipped.

Every archive contains `VERSION`, `RELEASE-SHA`, and a machine-readable
`RELEASE` key-value file. Verify an extracted archive against the release-level
checksums before installation:

```sh
sha256sum -c SHA256SUMS
```

## Version matrix and compatibility

The version numbers describe independently deployable components, not a rule
that all running components must have the same version.

| Component | Versioning unit | May upgrade independently? | Compatibility authority |
|---|---|---:|---|
| Task Server + Orchestrator Engine | One `agent-orchestrator` archive and one version. The two binaries are always built, installed, switched, and rolled back together. | No, not inside the pair | The Engine performs `POST /api/v1/protocol/compatibility` before it claims orchestration work. |
| Agent Host | One `agent-host` version across its RID payloads | Yes | Startup handshake with client kind `runner` or `review-runner`; an unsupported protocol is rejected before registration or claim. |
| Agent Studio | One static bundle version | Yes | The deployed Studio transport must identify client kind `studio` and use the runtime protocol contract. Missing or unsupported protocol headers are rejected before normal API access. |

Release tags currently cut all three repository assets together for a
reviewable initial release train. That does not create a runtime equality
requirement. A later component-only tag or repository split may advance Agent
Host or Agent Studio without changing the control-plane archive contract.
Compatibility comes only from the runtime protocol handshake, never from
comparing semantic versions or accepting unexplained HTTP 409 responses.

## First install

Extract the control-plane asset and run its installer as root. Passing a version
instead of a directory downloads that tag-bound GitHub Release and verifies its
entry in `SHA256SUMS`.

```sh
sudo ./install.sh v0.1.0
```

The installer is idempotent. It:

1. creates the `agent-orchestrator` system user and persistent directories;
2. stages the immutable tree at `/opt/agent-orchestrator/X.Y.Z`;
3. creates `/opt/agent-orchestrator/current` atomically;
4. guides the operator through first-time `server.env` and `engine.env`
   creation, but never overwrites existing configuration;
5. installs and enables Task Server, Engine, and backup units;
6. starts Task Server first, opens normal admission only after `/readyz`, and
   then starts the Engine.

State stays under `/var/lib/agent-orchestrator`; configuration stays under
`/etc/agent-orchestrator`. Neither lives inside a version directory. Caddy is
host infrastructure. The archive supplies a template but does not silently
install or reconfigure Caddy.

For unattended installation, set `NONINTERACTIVE=1` and provide `LISTEN_URL`,
`AUTH_MODE`, and, for bearer mode, optionally `AUTH_TOKEN`. The default is a
loopback listener with a generated 256-bit bearer credential. Selecting
`AUTH_MODE=none` is accepted only for a loopback listener.

## Update

Run the updater from the installed `current` tree or from the extracted
candidate:

```sh
sudo /opt/agent-orchestrator/current/update.sh v0.1.1
```

The updater follows this order:

1. set Task Server mode to `Draining`, which stops new coding, review, and
   orchestration claims while allowing lease renewal, completion, and release;
2. poll `prepare-shutdown` until every active or process-unknown authority is
   settled. Runner lease expiry and lease-loss process termination remain the
   execution-plane safety boundary;
3. stop Engine, then Task Server;
4. atomically switch `current` to the staged version;
5. start Task Server and require a green `/readyz`;
6. restore `Normal` mode, require `/readyz` to report `Normal`, then start the
   Engine.

The default drain timeout is 900 seconds and can be changed with
`DRAIN_TIMEOUT_SECONDS`. A timeout restores `Normal` mode and changes no link.
If the candidate stays red for `READY_TIMEOUT_SECONDS`, the updater stops it,
restores the old link, starts the old release, reopens admission only after its
own readiness gate, and exits nonzero. A failed candidate is never reported as
a successful update.

## Rollback

The last successful update records the former target in the `previous`
symlink. Roll it back with:

```sh
sudo /opt/agent-orchestrator/current/rollback.sh
```

An explicit installed version is also accepted. Rollback uses the same drain
and readiness gates as update. If the rollback target is unhealthy, the
original release is restored automatically and the command exits nonzero.
Store migrations must therefore remain backward-tolerant for at least one
release, as required by the distributable architecture.

## Honest CI contract

For release CI, green means green:

- no required test, lint, type-check, build, packaging, checksum, attestation,
  or release step uses `continue-on-error`;
- shell steps use failing exit codes and pipelines that propagate failure;
- .NET solution tests, release-topology tests, frontend lint/type-check/unit
  tests, and the production frontend build all block publication;
- the `Category!=MachineBound` filter is explicit because tests that inspect a
  particular live checkout are not hermetic release evidence;
- the GitHub Release step runs last and refuses an absent or mismatched tag.

Warnings may be visible, but they cannot hide a failing command. A release
badge means every declared release gate completed successfully on the exact
tagged SHA. This is the AGT-2306 rule applied to the release path.

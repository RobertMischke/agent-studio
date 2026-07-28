# The control plane as a distributable

**Elaborated concept page:** `distributable.html` - packages, binaries, install/run,
target VM picture, and the four sign-off decisions D1-D4.

**Open question (raised by Robert, 2026-07-24, on AGT-2277):** should the control
plane be extracted into its own distributable *before* it is hosted on the
public Hetzner VM? Leaning: yes — sharpen this picture first, then deploy.

## Why extract first

- Deploying today's all-in-one `OrchestratorApi` binary would ship the un-split
  monolith to a public origin and cement exactly the coupling the actor model
  removes (see restart-matrix.md).
- A public, authenticated deployment wants a *versioned artifact* with release
  semantics — like `coding-agent-chat` 0.3.x: tagged, built by CI, provenance,
  rollback = previous version — not "our checkout plus systemd recipes".

## What the distributable contains (proposal)

| Piece | In the package |
|---|---|
| Task Server unit | binary + systemd unit + config contract (env/file), private listener |
| Orchestrator Engine unit | own binary/unit, API-client credentials, no shared state |
| Migrations | store schema/layout migrations, idempotent, run on start |
| Backup/restore | scheduled backup of task repository + security state; verified restore path (AGT-2194 groundwork) |
| Edge | Caddy config template (only 80/443 public), `networked` security profile |
| Not included | Studio dev server, Runner (separate distributable per host), any repo checkout |

## Consequences

- AGT-2277 (host the control plane on the Hetzner VM) is **parked** until this
  definition is decided; it then gets re-cut as "deploy the distributable".
- The migration path (migration-path.md) now distinguishes the operational
  Tranche 0 Review mount from the public distributable. Mounting the stable
  `/api/v1` Review contract in `OrchestratorApi` restores Remote Review against
  the existing authority without publishing the monolith as the target
  control-plane package.
- Building the versioned distributable remains a prerequisite for Tranche 1 and
  also serves local installation with the same package and different config.
- The local OSS entry uses Docker Compose as its sole documented installation
  path. It builds the current Studio and API containers from one checkout and
  waits on their health contract. The three release archives remain the
  production distributable boundary, not a competing getting-started route.
  The rationale and clean-host evidence live in
  [setup-scenarios.md](./setup-scenarios.md).
- On Linux runner hosts, `agent-host` owns role-specific cgroup controls in the
  main Coding and Review units on every install and update. Values follow
  host-derived defaults unless the operator deliberately changes
  `/etc/agent-host/profile.conf`; see
  [runner-host resource governance](resource-governance.md).

## Token requirements and guided onboarding

The D5 guided installer line (AGT-2334) must include a **Create token** step
before it stores repository credentials. The installer implementation remains
on that card; this document defines its connection to the runner contract.

The step links to
[Linux runner host: Token requirements](../../setup/linux-runner-host.md#token-requirements)
and presents the exact GitHub choices:

- Fine-grained PAT, preferred: select the organization that owns the repository
  as resource owner, limit access to assigned repositories, and grant
  **Contents: Read and write** plus **Workflows: Read and write**.
- Classic PAT, compatibility fallback: grant **`repo`** plus **`workflow`** and
  complete organization SSO authorization when applicable.
- A PAT belongs to its creating user, not to the organization. Prefer a
  dedicated machine account; organization policy may require owner approval.
- With `credential.https://github.com.useHttpPath=true`, store and verify the
  credential for both `https://github.com/OWNER/REPOSITORY` and
  `https://github.com/OWNER/REPOSITORY.git`.

The wizard waits for the runner's startup result and explains all three states:
`ready`, `ready-no-workflow-scope`, and `read-only`. Only `read-only` blocks
claims. The middle state links to the permission checklist but does not try to
predict whether a card will touch `.github/workflows`. Rotation repeats both URL
entries and both checks before the old token is revoked.

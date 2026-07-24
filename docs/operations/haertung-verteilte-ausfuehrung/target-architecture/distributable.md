# The control plane as a distributable

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
- The migration path (migration-path.md) gains a tranche 0: "define + build the
  distributable" — which also serves the local installation (same package,
  different config).

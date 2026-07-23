# AGT-2194 delivery status

## Basis and salvage

- Reset the assigned task branch to `origin/develop` at `6c27c9b3`.
- Audited salvage commit `14c60bec7e3abc10be66a02425d07f37bebb110d`.
- Recovered the task-owned management and Task Server UI changes.
- Excluded stale copies of AGT-2193 security work and unrelated branch drift.

## Tranche 1

Delivered and committed first as `855e814c`:

- Authenticated live status at `/api/v1/management/status`.
- Server identity, URL, version, protocol range, uptime, readiness, health,
  maintenance, store totals, evidence totals, migrations, backups, security,
  and Runner projections.
- Audited, idempotent archive, orphan, and fixture sweeps.
- Dry-run previews and exact confirmation before apply.
- AGT-1924 Agent Studio UI wired to the live API.
- Production seed and simulated management actions removed.

The tranche gate passed before follow-up recovery work:

- Management API tests: 3 passed.
- Task Server frontend tests: 18 passed.

## Tranche 2

Recovered and integrated after the Tranche 1 gate:

- Server-hosted `/recovery` console using the same management API.
- Backup creation outside the live data directory, creation-time checksum
  manifests, isolated restore verification, retention, and failure state.
- Durable migration state tied to readiness.
- Maintenance and read-only admission enforcement.
- Shared AGT-2193 users, sessions, and Runner credential authority.
- Runner enrollment, credential rotation and revoke, drain, retire, last-seen,
  last-claim, and slot state without a second registry.
- Service-manager lifecycle ownership reported explicitly.

## Verification and evidence

- Focused management/security backend tests: 31 passed.
- Task Server frontend tests: 22 passed.
- Task Server Playwright spec: 6 passed.
- Full non-machine-bound solution gate: 4,343 passed and 12 skipped
  across backend, Task Server, and Runner test projects.
- Light and dark screenshots cover healthy, degraded, maintenance, migration,
  credential rotation, and failed backup states in this directory.

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

- Focused management/security backend tests: 32 passed.
- Task Server frontend tests: 22 passed.
- Task Server Playwright spec: 6 passed.
- Full non-machine-bound solution gate: 4,344 passed and 12 skipped
  across backend, Task Server, and Runner test projects.
- Light and dark screenshots cover healthy, degraded, maintenance, migration,
  credential rotation, and failed backup states in this directory.

The final re-cut verification on 2026-07-23 repeated the same solution and
frontend gates from the assigned worktree. Task Server ESLint and Stylelint
passed. The repository-wide frontend lint remains blocked by unrelated
`origin/develop` baseline violations outside the AGT-2194 diff.

The assigned worktree verification was repeated again on 2026-07-24:

- `dotnet test --filter "Category!=MachineBound"`: 4,344 passed, 12 skipped.
- Task Server Angular tests: 22 passed.
- Task Server Playwright spec: 6 passed.
- Task Server ESLint and strict Stylelint: passed.
- All 12 required state/theme screenshots were regenerated from this branch.
- A privileged-command normalization regression was added and the focused
  management/security gate passed all 32 tests.

The first full solution run shared the host with the frontend build and exposed
two unrelated load-sensitive test failures. Both passed in an immediate
isolated rerun, and the uncontended full solution rerun passed completely.

## Night-Ops fix round, 2026-07-24

- Closed the owner-command authorization bypass by normalizing command kind
  once in `ManagementService.Execute`, then using that exact value for the
  owner check, dispatch, fingerprint, and audit.
- Added a regression proving that an operator cannot execute whitespace-padded
  `runner-credential-rotate`; the request returns 403 before audit.
- Replaced the mislabeled Test/temp backup proof with a Production-hosted
  verification against an isolated server data directory under
  `/var/tmp/agent-studio-server-data/AGT-2194`.
- Verified backup creation outside the server data directory, checksum
  manifest publication, restore verification, and staging cleanup through the
  management API.
- Detailed evidence is in `management-fix-round-verification.md`.

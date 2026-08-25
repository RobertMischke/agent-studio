#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
assets=$repo_root/deploy/windows/task-server
installer=$assets/install-task-server.ps1
runner=$assets/run-task-server.ps1
control=$assets/task-server-control.ps1
rehearsal=$assets/rehearse-legacy-migration.ps1
invoke_migration=$assets/invoke-legacy-migration.ps1
configure_proxy=$assets/set-task-server-proxy.ps1
verify_cutover=$assets/verify-task-server-cutover.ps1

for file in \
  "$installer" \
  "$runner" \
  "$control" \
  "$rehearsal" \
  "$invoke_migration" \
  "$configure_proxy" \
  "$verify_cutover"; do
  test -s "$file"
done

grep -Fq -- '-LogonType S4U' "$installer"
grep -Fq 'New-ScheduledTaskTrigger -AtStartup' "$installer"
grep -Fq -- '-ExecutionTimeLimit ([TimeSpan]::Zero)' "$installer"
grep -Fq "Join-Path \$devspace 'task-server-data'" "$installer"
grep -Fq "Join-Path \$serviceRoot 'releases'" "$installer"
grep -Fq 'Installed Task Server identity does not contain commit' "$installer"
grep -Fq "'STORE_PATH'" "$rehearsal"
grep -Fq -- '-FreezeConfirmed' "$rehearsal"
grep -Fq 'ReadToEndAsync()' "$rehearsal"
grep -Fq 'EvidenceDirectory must be outside LegacySourceRoot' "$rehearsal"
grep -Fq 'expectedMigrationId' "$invoke_migration"
grep -Fq 'TaskServer' "$configure_proxy"
grep -Fq 'run.claimed' "$verify_cutover"
grep -Fq 'review.reported' "$verify_cutover"
grep -Fq 'current-release.txt' "$runner"
grep -Fq '/readyz' "$control"

printf '%s\n' 'task-server Windows deployment asset tests passed'

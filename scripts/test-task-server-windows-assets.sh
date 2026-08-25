#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
assets=$repo_root/deploy/windows/task-server
installer=$assets/install-task-server.ps1
registration=$assets/register-task-server.ps1
supervisor=$assets/start-task-server.ps1
rehearsal=$assets/rehearse-legacy-migration.ps1
proxy=$assets/set-stable-task-server-proxy.ps1

for asset in "$installer" "$registration" "$supervisor" "$rehearsal" "$proxy"; do
  test -s "$asset"
done

grep -Fq -- '-p:PublishProfile=win-x64' "$installer"
grep -Fq "Join-Path \$DevspaceDirectory 'task-server-data'" "$installer"
grep -Fq "'AgentOrchestrator-TaskServer'" "$installer"
grep -Fq -- '-LogonType S4U' "$registration"
grep -Fq 'New-ScheduledTaskTrigger -AtStartup' "$registration"
grep -Fq -- '-ExecutionTimeLimit ([TimeSpan]::Zero)' "$registration"
grep -Fq 'while ($true)' "$supervisor"
grep -Fq 'RestartDelaySeconds' "$supervisor"
grep -Fq 'runnerIdentities' "$rehearsal"
grep -Fq 'activeAuthorities' "$rehearsal"
grep -Fq 'expectedMigrationId' "$rehearsal"
grep -Fq 'TaskServer' "$proxy"
grep -Fq 'BaseUrl' "$proxy"

printf '%s\n' 'task-server Windows deployment asset tests passed'

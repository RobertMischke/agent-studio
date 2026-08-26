#!/usr/bin/env bash
# Windows Git Bash rehearsal for LocalCliRepairService. This intentionally
# removes only the selected npm launcher set, keeps recoverable backups, waits
# for the backend self-heal, and restores the originals if the rehearsal fails.

set -euo pipefail

cli="${1:-claude}"
api_url="${2:-http://127.0.0.1:5030}"
case "$cli" in
  claude) package='@anthropic-ai/claude-code' ;;
  codex) package='@openai/codex' ;;
  *) echo "usage: $0 {claude|codex} [backend-url]" >&2; exit 2 ;;
esac

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) ;;
  *) echo "refusing: this rehearsal is only for the Windows control-plane host" >&2; exit 2 ;;
esac

if [[ "${CONFIRM_CLI_SHIM_REHEARSAL:-}" != "$cli" ]]; then
  echo "refusing: set CONFIRM_CLI_SHIM_REHEARSAL=$cli to acknowledge the launcher move" >&2
  exit 2
fi

npm_bin="${APPDATA:?APPDATA is required}/npm"
package_dir="$npm_bin/node_modules/$package"
[[ -d "$package_dir" ]] || { echo "package is not installed at $package_dir" >&2; exit 1; }

backup_dir="$(mktemp -d)"
healed=0
restore() {
  if [[ "$healed" -eq 0 ]]; then
    for ext in '' '.cmd' '.ps1'; do
      [[ -f "$backup_dir/$cli$ext" ]] && mv -- "$backup_dir/$cli$ext" "$npm_bin/$cli$ext"
    done
  fi
  rm -rf -- "$backup_dir"
}
trap restore EXIT

before_version="$(node -e "process.stdout.write(require(process.argv[1]).version)" "$package_dir/package.json")"
rehearsal_started="$(date -u +%FT%TZ)"
echo "before package=$package version=$before_version observed=$rehearsal_started"

for ext in '' '.cmd' '.ps1'; do
  [[ -f "$npm_bin/$cli$ext" ]] && mv -- "$npm_bin/$cli$ext" "$backup_dir/$cli$ext"
done

deadline=$((SECONDS + 180))
while (( SECONDS < deadline )); do
  payload="$(curl -fsS "$api_url/api/runner/status" 2>/dev/null || true)"
  if node -e '
    const body = JSON.parse(process.argv[1]);
    const started = Date.parse(process.argv[3]);
    process.exit((body.cliRepairs || []).some(x =>
      x.cliType === process.argv[2]
      && x.outcome === "repaired"
      && Date.parse(x.attemptedAt) >= started
    ) ? 0 : 1);
  ' "$payload" "$cli" "$rehearsal_started" 2>/dev/null; then
    healed=1
    break
  fi
  sleep 2
done

if [[ "$healed" -ne 1 ]]; then
  echo "self-heal was not observed within 180 seconds; original shims will be restored" >&2
  exit 1
fi

[[ -f "$npm_bin/$cli.cmd" ]] || { echo "repair status arrived but $cli.cmd is absent" >&2; healed=0; exit 1; }
after_version="$("$npm_bin/$cli.cmd" --version | tr -d '\r' | head -1)"
echo "after cli=$cli version=$after_version observed=$(date -u +%FT%TZ)"
echo "runner status: $payload"
echo "journal: <TaskRepository>/.runtime/cli-repairs.jsonl"

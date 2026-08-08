#!/usr/bin/env bash
# Deploys one already-published agent-host release through the fixed staging
# root and the versioned least-privilege boundary. The release contains no
# credentials or environment files.
set -euo pipefail

host=""
release_dir=""
release_id=""
role="both"

usage() {
  cat <<'EOF'
Usage: remote-agent-host-deploy.sh \
  --host <ssh-alias-or-user@host> \
  --release-dir <published-agent-host-directory> \
  --release-id <immutable-id> \
  [--role <coding|review|both>]

The recipe stages files with scp, activates only the fixed /opt/agent-host
release path, restarts only agent-host.service and/or
agent-runner-review.service, and prints the bounded capability probe result.
EOF
}

die() {
  printf '[agent-host-deploy] ERROR: %s\n' "$*" >&2
  exit 2
}

while (($#)); do
  case "$1" in
    --host) host="${2:-}"; shift 2 ;;
    --release-dir) release_dir="${2:-}"; shift 2 ;;
    --release-id) release_id="${2:-}"; shift 2 ;;
    --role) role="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

host_pattern='^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$'
[[ "$host" =~ $host_pattern ]] || die "--host must be a configured alias or user@host"
[[ -d "$release_dir" ]] || die "--release-dir is not a directory"
[[ -f "$release_dir/agent-host" && -x "$release_dir/agent-host" ]] \
  || die "--release-dir does not contain an executable agent-host"
[[ "$release_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$ ]] \
  || die "--release-id contains unsupported characters"
[[ "$role" =~ ^(coding|review|both)$ ]] || die "--role must be coding, review, or both"
if find "$release_dir" -xdev \( -type l -o -type b -o -type c -o -type p -o -type s \) -print -quit \
    | grep -q .; then
  die "release directory contains a link or special file"
fi
if find "$release_dir" -xdev -perm /6022 -print -quit | grep -q .; then
  die "release directory contains a set-id or group/world-writable entry"
fi

ssh_base=(ssh -o BatchMode=yes -o ConnectTimeout=10)
scp_base=(scp -o BatchMode=yes -o ConnectTimeout=10)
remote_stage="/var/lib/agent-host-deploy/incoming/$release_id"

printf '[agent-host-deploy] phase=stage host=%s release=%s\n' "$host" "$release_id"
"${ssh_base[@]}" -T "$host" bash -s -- "$release_id" <<'REMOTE_STAGE'
set -euo pipefail
release_id="$1"
[[ "$release_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$ ]] || exit 20
stage="/var/lib/agent-host-deploy/incoming/$release_id"
rm -rf -- "$stage"
install -d -m 0750 "$stage"
REMOTE_STAGE
"${scp_base[@]}" -r -- "$release_dir/." "$host:$remote_stage/"

printf '[agent-host-deploy] phase=activate host=%s release=%s\n' "$host" "$release_id"
"${ssh_base[@]}" -T "$host" bash -s -- "$release_id" "$role" <<'REMOTE_ACTIVATE'
set -euo pipefail
release_id="$1"
role="$2"
sudo -n /usr/local/sbin/agent-host-admin activate "$release_id"

units=()
roles=()
if [[ "$role" == coding || "$role" == both ]]; then
  units+=(agent-host.service)
  roles+=(coding)
fi
if [[ "$role" == review || "$role" == both ]]; then
  units+=(agent-runner-review.service)
  roles+=(review)
fi

for index in "${!units[@]}"; do
  unit="${units[$index]}"
  service_role="${roles[$index]}"
  sudo -n /usr/bin/systemctl restart "$unit"
  sudo -n /usr/bin/systemctl status --no-pager "$unit"
  capability_line=""
  for _ in $(seq 1 30); do
    capability_line="$(sudo -n /usr/local/sbin/agent-host-admin capability "$service_role" 2>/dev/null || true)"
    [[ -n "$capability_line" ]] && break
    sleep 2
  done
  [[ -n "$capability_line" ]] || {
    printf 'No capability result was observed for %s after restart.\n' "$unit" >&2
    exit 40
  }
  printf '%s\n' "$capability_line"
done
REMOTE_ACTIVATE

printf '[agent-host-deploy] completed host=%s release=%s role=%s\n' \
  "$host" "$release_id" "$role"

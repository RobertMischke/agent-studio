#!/usr/bin/env bash
# Operator-run bootstrap for the agent-host least-privilege boundary.
# Run this from a separate root session after all coding and review work is
# drained. Do not run it through an agent CLI or from an unreviewed checkout.
set -euo pipefail

PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
export PATH

agent_user=""
revoke_file=""
restart_units=0

usage() {
  cat <<'EOF'
Usage: install-privilege-policy.sh --user <runner-account> \
  [--revoke-file /etc/sudoers.d/<legacy-file>] [--restart-units]

The optional legacy file is moved to /var/backups/agent-host-privilege after
the replacement policy passes visudo. A broad grant in /etc/sudoers or another
file is never edited automatically and makes the final audit fail.
EOF
}

die() {
  printf 'install-privilege-policy: %s\n' "$*" >&2
  exit 2
}

while (($#)); do
  case "$1" in
    --user) agent_user="${2:-}"; shift 2 ;;
    --revoke-file) revoke_file="${2:-}"; shift 2 ;;
    --restart-units) restart_units=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

[[ "$(id -u)" == 0 ]] || die "run from a separate root operator session"
[[ -z "${SUDO_USER:-}" || "${SUDO_USER:-}" == root ]] \
  || die "do not run this bootstrap through the runner account's existing sudo grant"
[[ "$agent_user" =~ ^[a-z_][a-z0-9_-]{0,30}$ ]] || die "invalid runner account"
id "$agent_user" >/dev/null 2>&1 || die "runner account does not exist: $agent_user"
if [[ -n "$revoke_file" ]]; then
  [[ "$revoke_file" =~ ^/etc/sudoers\.d/[A-Za-z0-9._-]+$ ]] \
    || die "--revoke-file must name one regular file directly below /etc/sudoers.d"
  [[ -f "$revoke_file" && ! -L "$revoke_file" ]] \
    || die "legacy sudoers file is not a regular file: $revoke_file"
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
admin_source="$script_dir/agent-host-admin"
sudoers_source="$script_dir/sudoers.agent-host"
resource_source="$repo_root/scripts/agent-host-resource-governance.sh"
if [[ ! -f "$resource_source" ]]; then
  resource_source="$script_dir/../agent-host-resource-governance.sh"
fi
[[ -f "$admin_source" && -f "$sudoers_source" && -f "$resource_source" ]] \
  || die "versioned policy assets are incomplete"

agent_group="$(id -gn "$agent_user")"
policy_target="/etc/sudoers.d/90-agent-host-$agent_user"
policy_candidate="$(mktemp)"
cleanup() {
  rm -f -- "$policy_candidate"
}
trap cleanup EXIT
sed "s/@AGENT_USER@/$agent_user/g" "$sudoers_source" >"$policy_candidate"
chmod 0440 "$policy_candidate"
visudo -cf "$policy_candidate" >/dev/null \
  || die "rendered sudoers policy did not pass visudo"

install -d -m 0755 -o root -g root /etc/agent-host /usr/local/libexec /usr/local/sbin
printf 'AGENT_HOST_USER=%s\n' "$agent_user" \
  | install -m 0600 -o root -g root /dev/stdin /etc/agent-host/admin.conf
install -m 0755 -o root -g root "$admin_source" /usr/local/sbin/agent-host-admin
install -m 0755 -o root -g root "$resource_source" /usr/local/libexec/agent-host-resource-governance
install -d -m 0750 -o root -g "$agent_group" /var/lib/agent-host-deploy
install -d -m 0750 -o "$agent_user" -g "$agent_group" \
  /var/lib/agent-host-deploy/incoming /var/lib/agent-host-deploy/config
install -m 0440 -o root -g root "$policy_candidate" "$policy_target"
visudo -c >/dev/null || die "complete sudoers configuration is invalid after policy installation"

if [[ -n "$revoke_file" ]]; then
  [[ "$revoke_file" != "$policy_target" ]] || die "replacement policy cannot revoke itself"
  backup_root="/var/backups/agent-host-privilege"
  install -d -m 0700 -o root -g root "$backup_root"
  backup_file="$backup_root/$(date -u +%Y%m%dT%H%M%SZ)-$(basename "$revoke_file")"
  mv -- "$revoke_file" "$backup_file"
  if ! visudo -c >/dev/null; then
    mv -- "$backup_file" "$revoke_file"
    die "sudoers became invalid; legacy file was restored"
  fi
  printf 'legacy-sudoers-backup=%s\n' "$backup_file"
fi

if getent group docker >/dev/null 2>&1 \
    && id -nG "$agent_user" | tr ' ' '\n' | grep -Fxq docker; then
  gpasswd -d "$agent_user" docker >/dev/null
fi

if ((restart_units == 1)); then
  for unit in agent-host.service agent-runner-review.service; do
    if systemctl is-active --quiet "$unit"; then
      systemctl restart "$unit"
    fi
  done
fi

sudo_listing="$(sudo -l -U "$agent_user")"
if printf '%s\n' "$sudo_listing" | grep -Eq 'NOPASSWD:[[:space:]]*ALL([[:space:],]|$)'; then
  die "a passwordless ALL grant remains in sudoers; remove it with visudo and rerun"
fi
printf '%s\n' "$sudo_listing" | grep -Fq /usr/local/sbin/agent-host-admin \
  || die "the scoped administration helper is missing from the effective sudo policy"
printf '%s\n' "$sudo_listing" | grep -Fq '/usr/bin/systemctl restart agent-host.service' \
  || die "the coding restart command is missing from the effective sudo policy"
if sudo -u "$agent_user" sudo -n /usr/bin/id -u >/dev/null 2>&1; then
  die "an unrelated passwordless root command is still allowed"
fi
if sudo -u "$agent_user" sudo -n /usr/bin/systemctl daemon-reload >/dev/null 2>&1; then
  die "unscoped systemctl daemon-reload is still allowed"
fi
[[ " $(id -nG "$agent_user") " != *" docker "* ]] \
  || die "runner account is still a member of the docker group"
if command -v docker >/dev/null 2>&1 \
    && sudo -u "$agent_user" docker version >/dev/null 2>&1; then
  die "runner account can still reach the rootful Docker API"
fi

docker_gid="$(getent group docker | cut -d: -f3 || true)"
remaining_processes=0
if [[ -n "$docker_gid" ]]; then
  agent_uid="$(id -u "$agent_user")"
  for status_file in /proc/[0-9]*/status; do
    [[ -r "$status_file" ]] || continue
    process_uid="$(awk '/^Uid:/ { print $2; exit }' "$status_file")"
    [[ "$process_uid" == "$agent_uid" ]] || continue
    process_groups="$(awk '/^Groups:/ { print " " $0 " "; exit }' "$status_file")"
    if [[ "$process_groups" == *" $docker_gid "* ]]; then
      remaining_processes=$((remaining_processes + 1))
    fi
  done
fi
((remaining_processes == 0)) \
  || die "$remaining_processes existing runner-account process(es) still hold the docker group; terminate old sessions/workers before acceptance"

/usr/local/sbin/agent-host-admin policy-check
printf 'least-privilege-policy=installed user=%s sudoers=%s docker-group=absent\n' \
  "$agent_user" "$policy_target"

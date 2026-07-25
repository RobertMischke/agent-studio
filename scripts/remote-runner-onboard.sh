#!/usr/bin/env bash
# Product-owned remote runner onboarding controller (AGT-2094).
#
# This controller is launched from the standard visible CLI task. All setup
# commands run over SSH on the selected host, while stdout/stderr remain in the
# canonical task conversation. The runner daemon is started only by systemd.
set -euo pipefail

host=""
server_url=""
topology=""
client_id=""
runner_id=""
auth_token_file="/etc/agent-runner/runner-auth-token"
runner_name=""
git_remote=""
git_push_remote=""
package_id="CodingAgentRunner"
runner_command="agent-runner"
minimum_version="0.5.0"
skip_auth=0

usage() {
  cat <<'EOF'
Usage: remote-runner-onboard.sh \
  --host <ssh-alias-or-user@host> \
  --server <task-server-url-visible-from-host> \
  --topology <central|tunnel|lan> \
  --client-id <optional-attribution-label> \
  --runner-id <owner-enrolled-runner-id> \
  --runner-name <runner-name> \
  --git-remote <fetch-origin-url> \
  --git-push-remote <write-origin-url> [options]

Options:
  --package-id <id>       NuGet DotnetTool package (default: CodingAgentRunner)
  --runner-command <cmd>  Installed tool command (default: agent-runner)
  --minimum-version <v>   Minimum accepted package version (default: 0.5.0)
  --auth-token-file <p>   Protected Runner credential file already on the host
  --skip-auth             Do not launch login flows; status checks still run
  -h, --help              Show this help

The Task Server URL is tested from the remote host before installation. A
workstation loopback URL is valid only when --topology tunnel is selected and
the URL names the tunnel listener on the remote host (normally port 15031).
EOF
}

die() {
  printf '[onboarding] ERROR: %s\n' "$*" >&2
  exit 2
}

while (($#)); do
  case "$1" in
    --host) host="${2:-}"; shift 2 ;;
    --server) server_url="${2:-}"; shift 2 ;;
    --topology) topology="${2:-}"; shift 2 ;;
    --client-id) client_id="${2:-}"; shift 2 ;;
    --runner-id) runner_id="${2:-}"; shift 2 ;;
    --auth-token-file) auth_token_file="${2:-}"; shift 2 ;;
    --runner-name) runner_name="${2:-}"; shift 2 ;;
    --git-remote) git_remote="${2:-}"; shift 2 ;;
    --git-push-remote) git_push_remote="${2:-}"; shift 2 ;;
    --package-id) package_id="${2:-}"; shift 2 ;;
    --runner-command) runner_command="${2:-}"; shift 2 ;;
    --minimum-version) minimum_version="${2:-}"; shift 2 ;;
    --skip-auth) skip_auth=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "Unknown argument '$1'. Run with --help." ;;
  esac
done

[[ -n "$host" ]] || die "--host is required."
[[ -n "$server_url" ]] || die "--server is required."
[[ -n "$topology" ]] || die "--topology is required."
[[ -n "$runner_name" ]] || die "--runner-name is required."
[[ -n "$git_remote" ]] || die "--git-remote is required."
[[ -n "$git_push_remote" ]] || die "--git-push-remote is required."

host_pattern='^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$'
server_url_pattern='^https?://(\[[0-9A-Fa-f:]+\]|[A-Za-z0-9.-]+)(:[0-9]{1,5})?(/[A-Za-z0-9._~:/%+-]*)?$'
https_git_pattern='^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?/[A-Za-z0-9._~/%+@:-]+$'
ssh_git_pattern='^ssh://([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9.-]+(:[0-9]{1,5})?/[A-Za-z0-9._~/%+@:-]+$'
scp_git_pattern='^[A-Za-z0-9][A-Za-z0-9._-]*@[A-Za-z0-9.-]+:[A-Za-z0-9._~/%+@:-]+$'

[[ "$host" =~ $host_pattern ]] || die "The SSH target contains unsupported characters. Use a configured alias or user@host."
[[ "$topology" =~ ^(central|tunnel|lan)$ ]] || die "--topology must be central, tunnel, or lan."
[[ "$server_url" =~ $server_url_pattern ]] || die "--server must be an http(s) URL without embedded credentials, fragments, whitespace, or shell characters."
[[ -z "$client_id" || "$client_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "--client-id contains unsupported characters."
[[ -z "$runner_id" || "$runner_id" =~ ^runner_[A-Za-z0-9_-]+$ ]] || die "--runner-id must be the runner_<id> returned by enrollment."
[[ "$auth_token_file" =~ ^/[A-Za-z0-9._/-]+$ ]] || die "--auth-token-file must be an absolute path without shell characters."
[[ "$runner_name" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "--runner-name contains unsupported characters."
[[ "$package_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "--package-id contains unsupported characters."
[[ "$runner_command" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "--runner-command contains unsupported characters."
[[ "$minimum_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]] || die "--minimum-version is not a semantic version."
if [[ ! "$git_remote" =~ $https_git_pattern \
      && ! "$git_remote" =~ $ssh_git_pattern \
      && ! "$git_remote" =~ $scp_git_pattern ]]; then
  die "--git-remote must be a credential-free SSH or HTTPS origin URL."
fi
if [[ ! "$git_push_remote" =~ $ssh_git_pattern \
      && ! "$git_push_remote" =~ $scp_git_pattern \
      && ! "$git_push_remote" =~ $https_git_pattern ]]; then
  die "--git-push-remote must be a credential-free SSH or HTTPS origin URL."
fi

case "$server_url" in
  http://localhost:*|https://localhost:*|http://127.0.0.1:*|https://127.0.0.1:*)
    [[ "$topology" == "tunnel" ]] || die \
      "'$server_url' is loopback from the runner's point of view. Choose a central/LAN URL, or select tunnel and provide the remote tunnel listener (normally http://127.0.0.1:15031)."
    ;;
esac

service_auth=0
if [[ "$topology" != "tunnel" ]]; then
  [[ "$server_url" == https://* ]] || die "Central and LAN Task Server URLs must use HTTPS."
  [[ -n "$runner_id" ]] || die "--runner-id is required for a networked Task Server. Enroll the Runner as an owner first."
  service_auth=1
else
  runner_id="${runner_id:-$runner_name}"
  [[ -n "$client_id" ]] || die "--client-id is required for the local profile reached through a tunnel."
fi

printf '[onboarding] host=%s runner=%s client=%s topology=%s server=%s\n' \
  "$host" "$runner_name" "${client_id:-none}" "$topology" "$server_url"

ssh_base=(ssh -o BatchMode=yes -o ConnectTimeout=10)

printf '[onboarding] phase=preflight Checking SSH, sudo, .NET, and Task Server reachability from the host.\n'
"${ssh_base[@]}" -T "$host" bash -s -- "$server_url" "$client_id" "$runner_id" "$auth_token_file" "$service_auth" <<'REMOTE_PREFLIGHT'
set -euo pipefail
server_url="$1"
client_id="$2"
runner_id="$3"
auth_token_file="$4"
service_auth="$5"
printf '[remote] connected host=%s user=%s\n' "$(hostname)" "$(id -un)"
command -v sudo >/dev/null || { echo '[remote] sudo is required.' >&2; exit 20; }
sudo -n true || { echo '[remote] passwordless sudo is required for unattended systemd installation.' >&2; exit 21; }
command -v dotnet >/dev/null || { echo '[remote] .NET 10 is missing.' >&2; exit 22; }
dotnet_version="$(dotnet --version)"
[[ "$dotnet_version" == 10.* ]] || { printf '[remote] .NET 10 is required; found %s.\n' "$dotnet_version" >&2; exit 23; }
command -v curl >/dev/null || { echo '[remote] curl is missing.' >&2; exit 24; }
health_url="${server_url%/}/healthz"
if ! curl --fail --show-error --silent --max-time 10 "$health_url" >/dev/null; then
  printf '[remote] Task Server is not reachable at %s.\n' "$health_url" >&2
  echo '[remote] Configure a central URL, a supervised reverse tunnel, or a protected LAN binding, then retry.' >&2
  exit 25
fi
printf '[remote] Task Server reachable: %s; dotnet=%s\n' "$health_url" "$dotnet_version"

if [[ "$service_auth" == 1 ]]; then
  sudo test -f "$auth_token_file" || { printf '[remote] Runner credential file is missing: %s\n' "$auth_token_file" >&2; exit 26; }
  token="$(sudo cat "$auth_token_file")"
  [[ "$token" == rnr.* ]] || { echo '[remote] Runner credential file does not contain an rnr.* service credential.' >&2; exit 27; }
  umask 077
  curl_config="$(mktemp)"
  trap 'rm -f "$curl_config"' EXIT
  printf 'header = "Authorization: Bearer %s"\n' "$token" >"$curl_config"
  identity_url="${server_url%/}/api/auth/runner"
  identity_json="$(curl --config "$curl_config" --fail --show-error --silent --max-time 10 "$identity_url")" || {
    echo '[remote] Runner service credential was not accepted.' >&2; exit 28;
  }
  printf '%s' "$identity_json" | grep -Fq "\"id\":\"$runner_id\"" || {
    printf '[remote] Credential identity does not match requested Runner id %s.\n' "$runner_id" >&2; exit 29;
  }
  printf '[remote] Runner service identity verified: %s\n' "$runner_id"
else
  identity_url="${server_url%/}/api/clients/${client_id}"
  curl --fail --show-error --silent --max-time 10 -H "X-Client-Id: $client_id" "$identity_url" >/dev/null || {
    printf '[remote] Local-profile client attribution %s was not found.\n' "$client_id" >&2; exit 26;
  }
fi
REMOTE_PREFLIGHT

printf '[onboarding] phase=install Installing/updating the runner tool and agent CLIs.\n'
if ! "${ssh_base[@]}" -T "$host" bash -s -- "$package_id" "$runner_command" "$minimum_version" <<'REMOTE_INSTALL'
set -euo pipefail
package_id="$1"
runner_command="$2"
minimum_version="$3"
export PATH="$HOME/.dotnet/tools:$HOME/.local/bin:$PATH"

current_version="$(dotnet tool list --global | awk -v id="$package_id" 'tolower($1) == tolower(id) { print $2; exit }')"
if [[ -z "$current_version" ]]; then
  dotnet tool install --global "$package_id"
else
  dotnet tool update --global "$package_id"
fi
installed_version="$(dotnet tool list --global | awk -v id="$package_id" 'tolower($1) == tolower(id) { print $2; exit }')"
[[ -n "$installed_version" ]] || { printf '[remote] NuGet package %s did not register as a global tool.\n' "$package_id" >&2; exit 30; }
lowest="$(printf '%s\n%s\n' "$minimum_version" "$installed_version" | sort -V | head -n 1)"
[[ "$lowest" == "$minimum_version" ]] || { printf '[remote] Runner tool %s is below required %s (found %s).\n' "$package_id" "$minimum_version" "$installed_version" >&2; exit 31; }
command -v "$runner_command" >/dev/null || { printf '[remote] Tool package %s does not expose command %s.\n' "$package_id" "$runner_command" >&2; exit 32; }

command -v npm >/dev/null || { echo '[remote] Node.js/npm is missing. Install Node 22, then retry.' >&2; exit 33; }
if ! npm install --global @openai/codex @anthropic-ai/claude-code; then
  echo '[remote] User-level npm global install failed; retrying through passwordless sudo.' >&2
  sudo -n npm install --global @openai/codex @anthropic-ai/claude-code
fi
printf '[remote] runner-package=%s runner-version=%s\n' "$package_id" "$installed_version"
codex --version
claude --version
REMOTE_INSTALL
then
  die "Runner installation failed. Verify that '$package_id' version $minimum_version or newer is published as a NuGet package with package type DotnetTool and exposes '$runner_command'. The CodingAgentRunner 0.5.0 library reference alone is not installable with 'dotnet tool'."
fi

remote_login_status() {
  "${ssh_base[@]}" -T "$host" bash -s <<'REMOTE_AUTH_STATUS'
set -uo pipefail
export PATH="$HOME/.dotnet/tools:$HOME/.local/bin:$PATH"
codex_ok=0
claude_ok=0
echo '[remote] Codex authentication status:'
codex login status && codex_ok=1
echo '[remote] Claude authentication status:'
claude auth status --text && claude_ok=1
((codex_ok == 1 && claude_ok == 1))
REMOTE_AUTH_STATUS
}

printf '[onboarding] phase=oauth Checking host-owned CLI authentication.\n'
if ! remote_login_status; then
  ((skip_auth == 0)) || die "One or more agent CLIs are not authenticated and --skip-auth was selected."

  printf '[onboarding] oauth=codex Open the URL shown below in the operator browser and enter the one-time device code.\n'
  "${ssh_base[@]}" -tt "$host" "export PATH=\"\$HOME/.dotnet/tools:\$HOME/.local/bin:\$PATH\"; codex login status || codex login --device-auth; codex login status"

  printf '[onboarding] oauth=claude Complete the URL/browser flow locally. Credentials remain on this host.\n'
  "${ssh_base[@]}" -tt "$host" "export PATH=\"\$HOME/.dotnet/tools:\$HOME/.local/bin:\$PATH\"; claude auth status --text || claude auth login --claudeai; claude auth status --text"

  remote_login_status || die "Authentication did not verify. Re-run setup; never copy credential files from another host."
fi

printf '[onboarding] phase=systemd Writing configuration and enabling the OS-owned service.\n'
"${ssh_base[@]}" -T "$host" bash -s -- \
  "$server_url" "$client_id" "$runner_id" "$runner_name" "$git_remote" "$git_push_remote" "$runner_command" "$auth_token_file" "$service_auth" <<'REMOTE_SYSTEMD'
set -euo pipefail
server_url="$1"
client_id="$2"
runner_id="$3"
runner_name="$4"
git_remote="$5"
git_push_remote="$6"
runner_command="$7"
auth_token_file="$8"
service_auth="$9"
export PATH="$HOME/.dotnet/tools:$HOME/.local/bin:$PATH"
runner_bin="$(command -v "$runner_command")"
runner_user="$(id -un)"
runner_group="$(id -gn)"
runner_home="$HOME"

env_tmp="$(mktemp)"
unit_tmp="$(mktemp)"
trap 'rm -f "$env_tmp" "$unit_tmp"' EXIT
chmod 600 "$env_tmp"
{
  printf 'RUNNER_SERVER_URL=%s\n' "$server_url"
  [[ -z "$client_id" ]] || printf 'RUNNER_CLIENT_ID=%s\n' "$client_id"
  printf 'RUNNER_ID=%s\n' "$runner_id"
  printf 'RUNNER_NAME=%s\n' "$runner_name"
  [[ "$service_auth" == 1 ]] && printf 'RUNNER_AUTH_TOKEN_FILE=%s\n' "$auth_token_file"
  printf 'RUNNER_GIT_REMOTE=%s\n' "$git_remote"
  printf 'RUNNER_GIT_PUSH_REMOTE=%s\n' "$git_push_remote"
  printf 'RUNNER_WORKDIR=/var/lib/agent-runner/work\n'
  printf 'RUNNER_STATE_DIR=/var/lib/agent-runner/state\n'
  printf 'RUNNER_MAX_PARALLELISM=2\n'
} >"$env_tmp"

cat >"$unit_tmp" <<EOF
[Unit]
Description=Agent Studio remote runner daemon
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=300
StartLimitBurst=5

[Service]
Type=simple
User=$runner_user
Group=$runner_group
WorkingDirectory=/var/lib/agent-runner
Environment=HOME=$runner_home
Environment="PATH=$runner_home/.dotnet/tools:$runner_home/.local/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
EnvironmentFile=/etc/agent-runner/runner.env
ExecStart=$runner_bin --poll
Restart=always
RestartSec=10s
TimeoutStopSec=90s
KillSignal=SIGTERM
KillMode=process
SyslogIdentifier=agent-runner
StandardOutput=journal
StandardError=journal
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ReadWritePaths=/var/lib/agent-runner $runner_home

[Install]
WantedBy=multi-user.target
EOF

sudo install -d -m 0750 /etc/agent-runner /var/lib/agent-runner /var/lib/agent-runner/work /var/lib/agent-runner/state
sudo chown -R "$runner_user:$runner_group" /var/lib/agent-runner
if [[ "$service_auth" == 1 ]]; then
  sudo chown root:"$runner_group" "$auth_token_file"
  sudo chmod 0640 "$auth_token_file"
fi
sudo install -m 0640 -o root -g "$runner_group" "$env_tmp" /etc/agent-runner/runner.env
sudo install -m 0644 "$unit_tmp" /etc/systemd/system/agent-runner.service
sudo systemctl daemon-reload
sudo systemctl enable agent-runner
sudo systemctl restart agent-runner
sleep 2
sudo systemctl is-enabled agent-runner
sudo systemctl is-active agent-runner
RUNNER_AUTH_TOKEN_FILE="$([[ "$service_auth" == 1 ]] && printf '%s' "$auth_token_file")" \
  "$runner_bin" --health-check --server "$server_url"

git_status=""
for _ in $(seq 1 30); do
  journal="$(sudo journalctl -u agent-runner -n 80 --no-pager)"
  printf '%s' "$journal" | grep -Fq 'runner-git-capability status=ready' && { git_status=ready; break; }
  printf '%s' "$journal" | grep -Fq 'runner-git-capability status=read-only' && { git_status=read-only; break; }
  sleep 2
done
[[ "$git_status" == ready ]] || {
  sudo journalctl -u agent-runner -n 40 --no-pager >&2
  printf '[remote] Runner Git push capability is %s; claims remain disabled.\n' "${git_status:-unreported}" >&2
  exit 40
}
printf '[remote] service=active health=passed identity=%s gitPushStatus=ready\n' "$runner_id"
REMOTE_SYSTEMD

printf '[onboarding] completed host=%s runner=%s client=%s\n' "$host" "$runner_name" "$client_id"
printf '[onboarding] Next: assign the probe project to %s and run one Ready task through the fenced remote claim path.\n' "$runner_name"

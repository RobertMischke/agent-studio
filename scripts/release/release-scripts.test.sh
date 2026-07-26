#!/bin/sh

set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM

publish_root="$test_root/publish"
frontend_root="$test_root/frontend/browser"
output_root="$test_root/artifacts"
for directory in task-server orchestrator-engine agent-host-linux-x64 agent-host-osx-arm64
do
    install -d -m 0755 "$publish_root/$directory"
done
install -d -m 0755 "$frontend_root"
for executable in \
    task-server/task-server \
    orchestrator-engine/orchestrator-engine \
    agent-host-linux-x64/agent-host \
    agent-host-osx-arm64/agent-host
do
    printf '#!/bin/sh\nexit 0\n' >"$publish_root/$executable"
    chmod 0755 "$publish_root/$executable"
done
printf '<!doctype html><title>Agent Studio</title>\n' >"$frontend_root/index.html"

SOURCE_DATE_EPOCH=1 "$repo_root/scripts/release/package-release.sh" \
    1.2.3 0123456789abcdef "$publish_root" "$frontend_root" "$output_root"
(
    cd "$output_root"
    [ "$(find . -maxdepth 1 -name '*.tar.gz' | wc -l)" -eq 3 ]
    sha256sum -c SHA256SUMS
    tar -tzf agent-orchestrator-1.2.3-linux-x64.tar.gz \
        | grep -q 'agent-orchestrator-1.2.3-linux-x64/update.sh'
    tar -tzf agent-host-1.2.3.tar.gz | grep -q 'agent-host-1.2.3/osx-arm64/agent-host'
    tar -tzf agent-studio-1.2.3.tar.gz | grep -q 'agent-studio-1.2.3/browser/index.html'
)

fake_systemctl="$test_root/systemctl"
printf '#!/bin/sh\nexit 0\n' >"$fake_systemctl"
chmod 0755 "$fake_systemctl"

fake_curl="$test_root/curl"
cat >"$fake_curl" <<'EOF'
#!/bin/sh
set -eu
method=GET
data=
url=
while [ "$#" -gt 0 ]; do
    case "$1" in
        -X) method=$2; shift 2 ;;
        --data) data=$2; shift 2 ;;
        -H) shift 2 ;;
        -*) shift ;;
        *) url=$1; shift ;;
    esac
done
mode_file=${FAKE_MODE_FILE:?}
mode=$(cat "$mode_file")
case "$url" in
    */readyz)
        active=$(basename "$(readlink -f "${AGENT_ORCHESTRATOR_OPT_ROOT:?}/current")")
        [ "$active" != "9.9.9" ] || exit 22
        printf '{"status":"ready","mode":"%s"}\n' "$mode"
        ;;
    */api/v1/management/mode)
        mode=$(printf '%s' "$data" | sed -n 's/.*"mode":"\([^"]*\)".*/\1/p')
        printf '%s\n' "$mode" >"$mode_file"
        printf '{"mode":"%s"}\n' "$mode"
        ;;
    */api/v1/management/prepare-shutdown)
        printf '%s\n' Maintenance >"$mode_file"
        printf '{"safeToStop":true,"unresolvedAttempts":0,"mode":"Maintenance"}\n'
        ;;
    *) printf 'unexpected URL: %s\n' "$url" >&2; exit 2 ;;
esac
EOF
chmod 0755 "$fake_curl"

make_release()
{
    version=$1
    destination="$test_root/source-$version"
    cp -a "$repo_root/deploy/release/agent-orchestrator" "$destination"
    printf '%s\n' "$version" >"$destination/VERSION"
    printf '#!/bin/sh\nexit 0\n' >"$destination/task-server"
    printf '#!/bin/sh\nexit 0\n' >"$destination/orchestrator-engine"
    chmod 0755 "$destination/task-server" "$destination/orchestrator-engine" \
        "$destination/install.sh" "$destination/update.sh" "$destination/rollback.sh" "$destination/lib.sh"
    printf '%s\n' "$destination"
}

opt_root="$test_root/opt"
config_root="$test_root/etc"
state_root="$test_root/state"
systemd_root="$test_root/systemd"
mode_file="$test_root/mode"
printf '%s\n' Normal >"$mode_file"
release_1=$(make_release 1.0.0)
release_2=$(make_release 1.1.0)
release_bad=$(make_release 9.9.9)

run_env()
{
    env \
        AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK=1 \
        AGENT_ORCHESTRATOR_SKIP_USER_CREATE=1 \
        AGENT_ORCHESTRATOR_OPT_ROOT="$opt_root" \
        AGENT_ORCHESTRATOR_CONFIG_ROOT="$config_root" \
        AGENT_ORCHESTRATOR_STATE_ROOT="$state_root" \
        AGENT_ORCHESTRATOR_SYSTEMD_ROOT="$systemd_root" \
        AGENT_ORCHESTRATOR_MANAGEMENT_URL=http://127.0.0.1:5071 \
        SYSTEMCTL_BIN="$fake_systemctl" \
        CURL_BIN="$fake_curl" \
        FAKE_MODE_FILE="$mode_file" \
        NONINTERACTIVE=1 \
        READY_TIMEOUT_SECONDS=1 \
        DRAIN_TIMEOUT_SECONDS=2 \
        "$@"
}

run_env "$release_1/install.sh" "$release_1"
config_hash=$(sha256sum "$config_root/server.env")
printf '%s\n' Draining >"$mode_file"
run_env "$release_1/install.sh" "$release_1"
[ "$(sha256sum "$config_root/server.env")" = "$config_hash" ]
[ "$(basename "$(readlink -f "$opt_root/current")")" = "1.0.0" ]
[ "$(cat "$mode_file")" = "Draining" ]
printf '%s\n' Normal >"$mode_file"

run_env "$release_2/update.sh" "$release_2"
[ "$(basename "$(readlink -f "$opt_root/current")")" = "1.1.0" ]
[ "$(basename "$(readlink -f "$opt_root/previous")")" = "1.0.0" ]
[ "$(cat "$mode_file")" = "Normal" ]

if run_env "$release_bad/update.sh" "$release_bad"; then
    printf 'Unhealthy update unexpectedly succeeded.\n' >&2
    exit 1
fi
[ "$(basename "$(readlink -f "$opt_root/current")")" = "1.1.0" ]
[ "$(basename "$(readlink -f "$opt_root/previous")")" = "1.0.0" ]
[ "$(cat "$mode_file")" = "Normal" ]

run_env "$release_2/rollback.sh"
[ "$(basename "$(readlink -f "$opt_root/current")")" = "1.0.0" ]
[ "$(basename "$(readlink -f "$opt_root/previous")")" = "1.1.0" ]
[ "$(cat "$mode_file")" = "Normal" ]

printf 'Release packaging and install/update/rollback tests passed.\n'

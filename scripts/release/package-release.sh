#!/bin/sh

set -eu

usage()
{
    printf '%s\n' \
        "Usage: package-release.sh <version> <git-sha> <publish-root> <frontend-browser-root> <output-dir>" >&2
    exit 2
}

[ "$#" -eq 5 ] || usage
version=${1#v}
git_sha=$2
publish_root=$3
frontend_root=$4
output_dir=$5

case "$version" in
    ''|*[!0-9.]*|.*|*.) usage ;;
esac
old_ifs=$IFS
IFS=.
set -- $version
IFS=$old_ifs
[ "$#" -eq 3 ] || usage
case "$git_sha" in
    *[!0-9a-fA-F]*|'') usage ;;
esac
[ "${#git_sha}" -ge 7 ] || usage

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
for required in \
    "$publish_root/task-server/task-server" \
    "$publish_root/orchestrator-engine/orchestrator-engine" \
    "$publish_root/agent-host-linux-x64/agent-host" \
    "$publish_root/agent-host-osx-arm64/agent-host" \
    "$publish_root/setup/agent-orchestrator-setup" \
    "$frontend_root/index.html"
do
    [ -f "$required" ] || {
        printf 'Missing release input: %s\n' "$required" >&2
        exit 2
    }
done

work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT HUP INT TERM
install -d -m 0755 "$output_dir"

orchestrator="agent-orchestrator-$version-linux-x64"
host="agent-host-$version"
studio="agent-studio-$version"
install -d -m 0755 "$work/$orchestrator" "$work/$host/linux-x64" \
    "$work/$host/osx-arm64" "$work/$host/config" "$work/$host/systemd" \
    "$work/$host/privilege" \
    "$work/$studio/browser"

install -m 0755 "$publish_root/task-server/task-server" \
    "$work/$orchestrator/task-server"
install -m 0755 "$publish_root/orchestrator-engine/orchestrator-engine" \
    "$work/$orchestrator/orchestrator-engine"
cp -a "$repo_root/deploy/release/agent-orchestrator/." "$work/$orchestrator/"

install -m 0755 "$publish_root/agent-host-linux-x64/agent-host" \
    "$work/$host/linux-x64/agent-host"
install -m 0755 "$publish_root/agent-host-osx-arm64/agent-host" \
    "$work/$host/osx-arm64/agent-host"
install -m 0644 "$repo_root/deploy/release/agent-host/runner.env.template" \
    "$work/$host/config/runner.env.template"
install -m 0644 "$repo_root/deploy/systemd/agent-host.service" \
    "$work/$host/systemd/agent-host.service"
install -m 0755 "$repo_root/scripts/agent-host-resource-governance.sh" \
    "$work/$host/agent-host-resource-governance.sh"
install -m 0755 "$repo_root/deploy/agent-host/agent-host-admin" \
    "$work/$host/privilege/agent-host-admin"
install -m 0755 "$repo_root/deploy/agent-host/install-privilege-policy.sh" \
    "$work/$host/privilege/install-privilege-policy.sh"
install -m 0440 "$repo_root/deploy/agent-host/sudoers.agent-host" \
    "$work/$host/privilege/sudoers.agent-host"
install -m 0755 "$repo_root/scripts/remote-agent-host-deploy.sh" \
    "$work/$host/remote-agent-host-deploy.sh"

cp -a "$frontend_root/." "$work/$studio/browser/"

for component in "$orchestrator" "$host" "$studio"
do
    printf '%s\n' "$version" >"$work/$component/VERSION"
    printf '%s\n' "$git_sha" >"$work/$component/RELEASE-SHA"
done

printf '%s\n' \
    "component=agent-orchestrator" \
    "version=$version" \
    "gitSha=$git_sha" \
    "runtimeIdentifiers=linux-x64" \
    "protocolMinimum=1" \
    "protocolMaximum=2" \
    >"$work/$orchestrator/RELEASE"
printf '%s\n' \
    "component=agent-host" \
    "version=$version" \
    "gitSha=$git_sha" \
    "runtimeIdentifiers=linux-x64,osx-arm64" \
    "protocolVersion=2" \
    >"$work/$host/RELEASE"
printf '%s\n' \
    "component=agent-studio" \
    "version=$version" \
    "gitSha=$git_sha" \
    "protocolVersion=2" \
    >"$work/$studio/RELEASE"

archive()
{
    directory=$1
    archive_path=$2
    epoch=${SOURCE_DATE_EPOCH:-0}
    tar_path="$work/$directory.tar"
    tar --sort=name --mtime="@$epoch" --owner=0 --group=0 --numeric-owner \
        -C "$work" -cf "$tar_path" "$directory"
    gzip -n -c "$tar_path" >"$archive_path"
    rm -f -- "$tar_path"
}

archive "$orchestrator" "$output_dir/$orchestrator.tar.gz"
archive "$host" "$output_dir/$host.tar.gz"
archive "$studio" "$output_dir/$studio.tar.gz"
install -m 0755 "$publish_root/setup/agent-orchestrator-setup" \
    "$output_dir/agent-orchestrator-setup"
(
    cd "$output_dir"
    sha256sum \
        agent-orchestrator-setup \
        "$orchestrator.tar.gz" \
        "$host.tar.gz" \
        "$studio.tar.gz" \
        >SHA256SUMS
)

printf 'Created the guided setup executable, three release archives, and SHA256SUMS in %s\n' "$output_dir"

#!/bin/sh

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$SCRIPT_DIR/lib.sh"

require_root

requested=${1:-}
if [ -z "$requested" ]; then
    if [ -f "$SCRIPT_DIR/VERSION" ]; then
        requested=$SCRIPT_DIR
    else
        die "Usage: install.sh <vX.Y.Z|release-directory>"
    fi
fi

resolve_release_source "$requested" "$SCRIPT_DIR"
trap cleanup_resolved_source EXIT HUP INT TERM
source_dir=$RESOLVED_SOURCE
version=$(source_version "$source_dir")
already_active=0
if active_target=$(current_target 2>/dev/null); then
    if [ "$(basename "$active_target")" != "$version" ]; then
        die "Version $(basename "$active_target") is active. Use update.sh for a drained version change."
    fi
    already_active=1
fi

if [ "${AGENT_ORCHESTRATOR_SKIP_USER_CREATE:-0}" != "1" ] \
    && ! id "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --home-dir "$STATE_ROOT" --create-home \
        --shell /usr/sbin/nologin "$SERVICE_USER"
    log "Created system user $SERVICE_USER."
fi

install -d -m 0755 "$OPT_ROOT" "$CONFIG_ROOT" "$SYSTEMD_ROOT"
install -d -m 0750 "$STATE_ROOT" "$STATE_ROOT/backups"
if [ "${AGENT_ORCHESTRATOR_SKIP_USER_CREATE:-0}" != "1" ]; then
    chown -R "$SERVICE_USER:$SERVICE_USER" "$STATE_ROOT"
fi

prompt()
{
    label=$1
    default=$2
    variable=$3
    value=
    if [ "${NONINTERACTIVE:-0}" != "1" ] && [ -r /dev/tty ]; then
        printf '%s [%s]: ' "$label" "$default" >/dev/tty
        IFS= read -r value </dev/tty || true
    fi
    [ -n "$value" ] || value=$default
    eval "$variable=\$value"
}

escape_sed()
{
    printf '%s' "$1" | sed 's/[\/&]/\\&/g'
}

if [ ! -f "$CONFIG_ROOT/server.env" ]; then
    prompt "Private Task Server listen URL" "${LISTEN_URL:-http://127.0.0.1:5071}" listen_url
    prompt "Authentication mode (none or bearer)" "${AUTH_MODE:-bearer}" auth_mode
    case "$auth_mode" in
        none)
            token_file=
            token=
            ;;
        bearer)
            token_file="$CONFIG_ROOT/task-server.token"
            token=${AUTH_TOKEN:-}
            if [ -z "$token" ]; then
                command -v openssl >/dev/null 2>&1 \
                    || die "openssl is required to generate the initial bearer credential."
                token=$(openssl rand -hex 32)
            fi
            umask 077
            printf '%s\n' "$token" >"$token_file"
            chown "$SERVICE_USER:$SERVICE_USER" "$token_file" 2>/dev/null || true
            chmod 0640 "$token_file"
            ;;
        *) die "Authentication mode must be 'none' or 'bearer'." ;;
    esac

    sed \
        -e "s/@LISTEN_URL@/$(escape_sed "$listen_url")/" \
        -e "s/@STORE_PATH@/$(escape_sed "$STATE_ROOT")/" \
        -e "s/@BACKUP_PATH@/$(escape_sed "$STATE_ROOT/backups")/" \
        -e "s/@AUTH_MODE@/$(escape_sed "$auth_mode")/" \
        -e "s/@AUTH_TOKEN_FILE@/$(escape_sed "$token_file")/" \
        "$source_dir/config/server.env.template" >"$CONFIG_ROOT/server.env"
    chmod 0640 "$CONFIG_ROOT/server.env"

    sed \
        -e "s/@SERVER_URL@/$(escape_sed "$listen_url")/" \
        -e '/^CLIENT_CREDENTIAL=@CLIENT_CREDENTIAL@$/d' \
        "$source_dir/config/engine.env.template" >"$CONFIG_ROOT/engine.env"
    printf 'CLIENT_CREDENTIAL=%s\n' "$token" >>"$CONFIG_ROOT/engine.env"
    chmod 0640 "$CONFIG_ROOT/engine.env"
    chown "$SERVICE_USER:$SERVICE_USER" \
        "$CONFIG_ROOT/server.env" "$CONFIG_ROOT/engine.env" 2>/dev/null || true
    log "Created configuration from guided templates in $CONFIG_ROOT."
else
    [ -f "$CONFIG_ROOT/engine.env" ] \
        || die "$CONFIG_ROOT/server.env exists but engine.env is missing; refusing a partial configuration."
    log "Keeping existing operator configuration in $CONFIG_ROOT."
fi

target=$(install_release_tree "$source_dir")
if [ "$already_active" -eq 0 ]; then
    atomic_link "$target" "$OPT_ROOT/current"
fi

install_systemd_units "$target"
if [ "$already_active" -eq 1 ] \
    && "$SYSTEMCTL_BIN" is-active --quiet agent-task-server.service \
    && "$SYSTEMCTL_BIN" is-active --quiet agent-orchestrator-engine.service; then
    log "Release $version is already active; services and operator mode were left unchanged."
    install_result="Installation is already current at agent-orchestrator $version."
else
    if ! start_runtime; then
        die "Installed release $version did not become ready; inspect journalctl -u agent-task-server."
    fi
    install_result="Installed and started agent-orchestrator $version."
fi

log "$install_result"
log "Caddy remains host infrastructure. Template: $target/config/Caddyfile.template"

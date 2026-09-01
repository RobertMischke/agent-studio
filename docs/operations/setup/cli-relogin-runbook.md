# Runbook: renew provider authentication on an execution host

Use this runbook only when an Execution Hosts provider badge says **genuinely
signed out, re-auth needed**, a Ready card carries that confirmed reason, or a
run reports a probe-confirmed `ProviderUnauthorized`. A transient retry or rate
limit does not require re-authentication.

The authoritative host secret store is
`/etc/agent-runner/provider-auth.env`. It contains all environment-backed
provider credentials, is owned by `root:agent`, and has mode `640`. Both the
Coding and Review systemd units load it after their ordinary runner
EnvironmentFile. The probe never reads that protected EnvironmentFile. For
provider-owned OAuth logins it reads only non-secret refresh and expiry
timestamps from the CLI's home-directory credential file; token values are
never returned, logged, advertised, or stored by Studio.

## 1. Confirm the affected provider and host

Open **Workspace Settings > Execution Hosts** and inspect **Provider
authentication** on the affected host. The badge exposes these states:

- **OK**: a fresh capability snapshot reports usable provider authentication.
- **transient auth error, retrying**: the latest observation was indeterminate
  or is the first explicit logout response. Claims remain open while the runner
  retries and retains the last good state.
- **credentials expiring**: authentication works, but a known Claude refresh
  expiry is within 14 days or Codex has not refreshed for 30 days. This is a
  quiet warning and does not block claims.
- **genuinely signed out, re-auth needed**: consecutive distinguishable logout
  observations were confirmed. This is the only provider-auth state that raises
  the sign-in alarm and holds Ready cards.
- **Unavailable**: the CLI binary is missing or another non-auth capability is
  unusable. Hover the badge for detail; do not assume re-auth is the fix.
- **Unknown**: no current provider-auth advertisement exists, the advertisement
  is stale, or the runner is unreachable.

Provider transitions are retained in the capability recovery history. An
`OK -> genuinely signed out` transition creates an operator notification. A
run-level nonzero exit is only evidence: rate-limit output enters the limited
state, tool and patch errors stay with the run, and an auth signature triggers
an immediate status probe. The runner reports the capability failure only when
that probe supplies the required consecutive confirmation.

If the runner advertises a credential expiry, Studio warns once when it enters
the final 14 days. An absent expiry is reported as unknown and is never guessed
from the secret.

The installed Codex and Claude command surfaces expose non-interactive status
checks but no explicit non-interactive OAuth refresh command. The runner uses
those status checks as the safe validation point; if the CLI refreshes as part
of that check, the next metadata read observes it. Token rotation otherwise
remains owned by the CLI. The runner never starts a billable prompt or an
interactive login as a background freshness action.

## 2. Renew through Studio

1. Open the affected host in **Execution Hosts** and choose **Set up agent
   host**.
2. In **Provider authentication**, select `CLAUDE_CODE_OAUTH_TOKEN` or
   `ANTHROPIC_API_KEY` and paste the replacement value.
3. Choose **Provision and verify**.
4. Wait for **Latest runner probe: OK**. The dialog clears the input after both
   successful and failed requests.

Studio sends the value to the selected host through SSH stdin. It never places
the value on the SSH command line, in shell history, in the setup task, in the
Studio database, in logs, in the repository, or in `results/`. The host
atomically updates the shared file, restarts the installed Coding and Review
units, verifies only the variable name in `/proc/<MainPID>/environ`, and waits
for a newer runner probe.

Provisioning one Claude credential replaces the other Claude variable while
preserving entries for future providers in the shared file.

## 3. Verify recovery

The next capability advertisement normally arrives within 60 seconds. Recovery is
complete when all of these statements are true:

1. The host badge is **OK** and its timestamp is fresh.
2. The newest recovery-history entry records the successful provider probe and
   server-side health recovery.
3. Ready cards no longer show the provider sign-in wait reason.
4. A normal probe card can be claimed and completed on that host.

A card that already reached Human Review after an auth failure is not moved
automatically. Requeue it only after the host badge is **OK**.

If the EnvironmentFile installation succeeds but no new advertisement arrives,
inspect the Coding and Review unit state and the runner journal. Do not paste the
credential into a diagnostic command. The safe host-side checks are:

```bash
sudo stat -c '%U:%G %a %n' /etc/agent-runner/provider-auth.env
sudo systemctl is-active agent-host.service agent-runner-review.service
sudo journalctl -u agent-host.service -u agent-runner-review.service -n 100 --no-pager
```

## 4. Prohibited recovery paths

- Do not create a provider-specific `claude.env` file. There is exactly one
  provider EnvironmentFile.
- Do not copy `~/.claude/.credentials.json`, `~/.codex/auth.json`, or any other
  workstation credential file to the host.
- Do not put a token on an SSH command line, in a task prompt, or in a card.
- Do not edit the Studio database or repository to distribute a credential.
- Do not treat a systemd restart alone as renewal. Recovery requires a fresh
  provider-auth probe.

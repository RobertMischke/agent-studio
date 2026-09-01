# Runbook: renew provider authentication on an execution host

Use this runbook when an Execution Hosts provider badge changes from **OK** to
**Unavailable**, a Ready card says it is waiting for a provider sign-in, or a
run reports `ProviderUnauthorized`.

The authoritative environment-backed host secret store is
`/etc/agent-runner/provider-auth.env`. It contains all environment-backed
provider credentials, is owned by `root:agent`, and has mode `640`. Both the
Coding and Review systemd units load it after their ordinary runner
EnvironmentFile. A provider probe never reads that protected file. When a CLI
owns `~/.claude/.credentials.json` or `~/.codex/auth.json`, the probe may read
only non-secret expiry and refresh timestamps. It never logs or persists token
values.

## 1. Confirm the affected provider and host

Open **Workspace Settings > Execution Hosts** and inspect **Provider
authentication** on the affected host. The badge exposes five states:

- **OK**: a fresh capability snapshot reports usable provider authentication.
- **Retrying**: a transient auth error or the first distinguishable logout
  response retained last-good auth. Claims remain eligible.
- **Expiring**: the login still works, but a known re-auth deadline entered the
  final 14 days. Claims remain eligible.
- **Unavailable**: consecutive distinguishable auth failures confirmed that the
  CLI is genuinely signed out. Hover for the exact re-auth detail.
- **Unknown**: no current provider-auth advertisement exists, the advertisement
  is stale, or the runner is unreachable.

Provider transitions are retained in the capability recovery history. An
`OK -> Retrying` transition creates a quiet retry notification. Only a confirmed
`Unavailable` state creates the blocking re-auth notification. Rate-limit output
is shown as `limited until <time>` by the provider-limit banner, and normal tool
failures never change provider auth. Ready cards assigned to a genuinely
signed-out host show the same blocking reason.

If the runner advertises a credential expiry, Studio warns once when it enters
the final 14 days. An absent expiry is reported as unknown and is never guessed
from the secret.

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

The next degraded-state provider probe normally arrives within 60 seconds. Recovery is
complete when all of these statements are true:

1. The host badge is **OK** and its timestamp is fresh.
2. The newest recovery-history entry records the confirmed transition to
   `healthy` or `ready`.
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

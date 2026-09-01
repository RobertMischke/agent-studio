# Runbook: renew provider authentication on an execution host

Use this runbook only when an Execution Hosts provider badge says **Genuinely
signed out, re-auth needed**, a Ready card says it is waiting for provider
sign-in, or the runner has reached the consecutive explicit-auth-failure
threshold. Transient retry, rate-limit, and expiry-warning states do not require
immediate re-login.

The authoritative host secret store is
`/etc/agent-runner/provider-auth.env`. It contains all environment-backed
provider credentials, is owned by `root:agent`, and has mode `640`. Both the
Coding and Review systemd units load it after their ordinary runner
EnvironmentFile. A provider probe validates through the CLI and reads only
secret-free expiry and file-age metadata from host-local credential files. It
never returns token values.

## 1. Confirm the affected provider and host

Open **Workspace Settings > Execution Hosts** and inspect **Provider
authentication** on the affected host. The badge distinguishes these states:

- **Authenticated**: a fresh capability snapshot reports usable authentication.
- **Transient auth error, retrying**: an indeterminate probe or the first
  explicit auth failure retained the last-good capability. Claims remain open.
- **Rate-limited until &lt;time&gt;**: only the matching provider is paused and
  recovery is automatic. Do not re-authenticate for this state.
- **Credentials expiring**: the known hard expiry entered the final 14 days.
  This is a quiet, non-blocking renewal warning.
- **Genuinely signed out, re-auth needed**: consecutive explicit auth failures
  made the capability unavailable. This is the only sign-in alarm.
- **Unavailable**: the CLI binary or another required capability is missing.
- **Unknown**: no current provider-auth advertisement exists, the advertisement
  is stale, or the runner is unreachable.

Provider transitions are retained in capability recovery history. A run exit is
auth evidence only when a provider-owned terminal frame or stderr contains a
distinguishable auth signature. Tool errors, prompt text, rate limits, timeouts,
network failures, and generic nonzero exits cannot create a sign-in alarm. One
real auth rejection triggers an immediate independent status probe; only a
second explicit rejection closes admission. Ready cards then show the same
blocking reason.

The runner and operator may share one provider login. Refreshing that account on
one side can briefly invalidate the other side's access token. That window is a
transient retry and must not be handled by restarting the runner or copying
credential files.

If the runner advertises a credential expiry, Studio reports it once when it
enters the final 14 days. Access-token expiry is suppressed when refresh
material exists and no hard refresh expiry is known, avoiding hourly false
warnings.

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

The runner retries unavailable or degraded auth every minute. Recovery is
complete without a service restart when all of these statements are true:

1. The host badge is **OK** and its timestamp is fresh.
2. The newest recovery-history entry records `unavailable -> ready`.
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

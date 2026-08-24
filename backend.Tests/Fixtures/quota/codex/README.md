# Codex quota fixtures

One file per `codex-cli` version, each an ANSI-stripped PTY snapshot of the real
`/status` panel. `CodexQuotaProbeTests` parses every fixture with the production
`CodexQuotaProbe.ParseStatusWindows` / `ParsePlan`, so a codex release that moves
the panel around fails a test here instead of surfacing as a broken quota display
(AGT-2679).

| Fixture | codex-cli | Captured |
| --- | --- | --- |
| `codex-status-v0.144.1.txt` | 0.144.1 | 2026-08-24 |
| `codex-status-v0.149.0.txt` | 0.149.0 | 2026-08-24 |

## How these were captured

Both were taken from the real CLI against the same logged-in ChatGPT account
minutes apart, driving the TUI the way the probe does: a 160x40 PTY, spawn in an
isolated scratch directory, wait for the CLI to settle, send `/status`, submit
with a separate `Enter`, then strip ANSI with the same three regexes
`PtySession.SnapshotStripped` uses (CSI / OSC / two-byte ESC).

The 0.144.1 capture needed one extra keystroke: that build interposes an
"Update available!" picker whose default option is *1. Update now*, so the
capture selects *2. Skip* first. 0.149.0 renders the update notice as a passive
banner and needs no dismissal.

## What they show

Both versions render the same boxed panel - a `╭─…─╮` frame with `│`-delimited
rows - and both parse identically. Panel format drift between 0.144.1 and
0.149.0 is **not** what broke the operator's quota display; see AGT-2679 for the
actual cause (a probe deadline shorter than the probe's own step timeouts, plus a
background refresh that inherited the HTTP request's cancellation token).

Note the standard `5h limit:` row is absent from both captures. That is account
state, not drift: the account had no active 5-hour window, so codex omits the row
and the standard block is Weekly-only. The `GPT-5.3-Codex-Spark limit:` sub-block
below it still carries its own `5h limit:` / `Weekly limit:` rows, which is
exactly the layout the AGT-2064 Spark/standard split guards against
misattributing.

## Adding a version

Capture the panel, drop it in as `codex-status-v<version>.txt`, and add an
`[InlineData]` row to the theories in `backend.Tests/CodexQuotaProbeTests.cs`.
Keep older versions: they are the fallback evidence that a regex change made to
satisfy a new release did not break an older one.

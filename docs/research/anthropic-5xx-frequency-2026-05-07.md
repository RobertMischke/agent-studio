# Anthropic 5xx frequency — measurement before retry implementation

**Date:** 2026-05-07
**Window:** last 20 days (2026-04-17 → 2026-05-07)
**Scope:** all job folders under `<workspace>/projects/agent-taskboard/{4-auto-review,5-human-review,6-completed,7-archive}/*/`
**Method:** classify each job by `status.md` (`Result:` line) and tail of `logs/cli-output.log`. Detection regex for the 5xx signal:
- `API Error:\s*5\d\d` (the literal CLI stderr/stdout line the user observed)
- `"type":\s*"api_error"[^}]*"status":\s*5\d\d` (defensive against future structured-error format)
Cross-checked with a workspace-wide `grep` across **all** `cli-output.log` files for `API Error: 5xx` and the substrings `Internal server error`, `Overloaded`, `server-side issue`, `status.claude.com` to make sure no 5xx occurrence was missed by the classifier.

Raw classification: [`results/jobs-classification.csv`](../../../../agent-taskboard-workspace/projects/agent-taskboard/3-progress/bug-anthropic-5xx-retry-measure-first-then-decide/results/jobs-classification.csv) (in the job folder).

## Buckets

| Bucket | Count | Share of total | Share of Partials |
| --- | ---: | ---: | ---: |
| `success` | 87 | 40.7 % | — |
| `no-status-md` / `NoStatus`* | 111 | 51.9 % | — |
| `partial-other` | 8 | 3.7 % | 66.7 % |
| `failed` | 3 | 1.4 % | — |
| `partial-task-blocked` | 2 | 0.9 % | 16.7 % |
| `partial-watchdog` | 2 | 0.9 % | 16.7 % |
| `partial-anthropic-5xx` | **0** | **0.0 %** | **0.0 %** |
| `other-Die` (German status header) | 1 | 0.5 % | — |
| **Total** | **214** | 100 % | — |
| **Total Partials** | **12** | 5.6 % | 100 % |

\* 111 jobs in the window had no `Result:` line in `status.md` (the file was either missing, e.g. orchestrator/prep runs that don't write a status block, or contained a continuation prompt instead of a result writeup). They cannot be a `partial-anthropic-5xx` because they are not a Partial run-outcome at all. The cross-check (next paragraph) confirms none of them contain a 5xx error in their CLI log.

### Workspace-wide 5xx grep (not classifier-dependent)

A flat `grep` across **all** `cli-output.log` files in the workspace finds exactly **one** match for `API Error: 5\d\d`:

| Job | Lane | Date | Outcome of that run |
| --- | --- | --- | --- |
| `screenshots-in-editors` | `7-archive` | 2026-04-27 | `claude CLI exited: status=failed, exitCode=1` after the 500. The user **manually resumed** the same session ~37 min later (`session=499dff54-…` resume) and the work continued normally. The job did not land in `4-auto-review` as Partial — it was archived as accepted. |

That single 5xx incident is exactly the one in the user's bug report. There is no second occurrence in the 20-day window.

### Representative examples per bucket (≤ 5 each)

**`partial-anthropic-5xx`** — none in window.

**`partial-other`** (8)
- `cli-usage-models` (4-auto-review, 2026-05-03)
- `parallel-pipeline-phases-and-in-task-iteration` (4-auto-review, 2026-05-05)
- `project-chat-becomes-primary-surface-with-embedded-events` (4-auto-review, 2026-05-06)
- `chat-read--grep-wiederholunge-mit-weight-darstellen` (7-archive, 2026-05-03)
- `einzelner-tasks-starten-dann-kein-auto-button-aktivieren` (7-archive, 2026-05-01)

**`partial-watchdog`** (2)
- `das-sortieren-ist-buggy` (4-auto-review, 2026-05-03)
- `project-drift-control-surface` (4-auto-review, 2026-05-06)

**`partial-task-blocked`** (2)
- `bug-der-commit-haengt-am-end-to-end-test` (5-human-review, 2026-05-04)
- `arhciv-besser-darzustellen` (7-archive, 2026-05-01)

**`failed`** (3)
- `code-revie` (4-auto-review, 2026-05-05)
- `ich-moechte-dass-es-moeglich-ist-tasks-zu-loeschen-orphan-2026-05-05` (7-archive, 2026-05-02)
- `modal-default` (7-archive, 2026-04-28)

## Decision rule (from `prompt.md`)

> Implementation is justified when `partial-anthropic-5xx` is **≥ 20 % of all Partials** OR **≥ 5 separate jobs in the 20-day window**.

| Bar | Observed | Pass? |
| --- | --- | --- |
| ≥ 20 % of Partials | 0 / 12 = **0 %** | ❌ |
| ≥ 5 separate jobs in window (5xx in CLI log, regardless of bucket) | **1** | ❌ |

Both bars miss by a wide margin. Even if every `partial-other` were re-classified as `partial-anthropic-5xx` (which the workspace-wide grep rules out), the ≥ 5-jobs bar would still be the binding test, and only **one** job in 20 days has any 5xx evidence in its CLI log at all — and that one already self-recovered via manual session resume.

## Verdict

**Implementation NOT justified.**

In 20 days of operation across 214 runs and 12 Partials, the Anthropic-5xx signal appears exactly once. The existing watchdog (ADR-0030) plus loud-failure routing (`pickup-failures-loud-not-archived`) plus the manual-resume recovery path the user already used are sufficient. Adding a retry loop with its own counter, chip, banner, and Playwright spec would be **complexity for a once-in-three-weeks event** that did not even produce a stuck-Partial outcome.

If the rate climbs (e.g. an Anthropic incident week, or a client switch that depends on a less stable upstream), the same measurement script in [`results/analyze.ps1`](../../../../agent-taskboard-workspace/projects/agent-taskboard/3-progress/bug-anthropic-5xx-retry-measure-first-then-decide/results/analyze.ps1) can be re-run to revisit the decision. The verdict is binding **for this run**; per the prompt, a future operator may re-queue with `force-implement=true` to override.

### What stays in place (no change needed)

- `ClaudeCliService.StartAsync` plus the run-spawn path — unchanged.
- `ProjectRunner._consecutiveAutoFailureCount` and `AutoFailureHaltThreshold` — unchanged.
- Watchdog timing from ADR-0030 — unchanged.
- Loud-failure routing — unchanged.
- Operator workflow when a 5xx happens: the run lands stopped, watchdog kills it, job goes to `4-auto-review` Partial (or `5-human-review` after the 3-pickup loud-failure), operator either re-queues or resumes the session manually. Recovery is a few clicks and currently used at most once every 20 days.

## Caveats / what this measurement does not cover

- The window is 20 days. A larger window or a noisier upstream period could change the picture; re-run the script then.
- `no-status-md` jobs (51.9 %) include orchestrator-prep, archived-empty, and continuation-style entries; the cross-check grep covers their CLI logs, so they cannot hide a 5xx, but their bucket distribution is opaque to this analysis.
- Detection looks at stdout/stderr text only. If a future CLI version swallows the 5xx and exits silently, this measurement underestimates. We would notice the same way the user noticed this one — by reading a CLI log after a Partial — and re-run the script.

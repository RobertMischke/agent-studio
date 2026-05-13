---
name: sandbox-and-yolo
description: Per-CLI sandbox / permission / YOLO mode reference. What each agent's permission knob actually means, what the platform default is and why, the risk rating, and how to verify the effective mode at runtime. Embed inline in Project Settings under the agent-config surface.
sentinel: TASKBOARD-DOC-SANDBOX-YOLO-2026
---

<!-- SENTINEL: TASKBOARD-DOC-SANDBOX-YOLO-2026 — referenced from README §Principles, design-principles.md §Inline meta, and the in-product agent-config surface. -->

# Sandbox & YOLO modes per CLI

Each coding-agent CLI ships its own permission model. The orchestrated runner has to satisfy *all four* of them at once. This document is the canonical reference for what each mode means, what the platform defaults to, and why.

It is also the **inline-source** for the Project Settings agent-config surface: the short blurbs next to each toggle should render from the matching subsection below.

## Why we default to YOLO

The runner is **non-interactive**. A coding-agent CLI that pauses for an approval prompt — "Run this command? [y/N]" — has no human at the keyboard. The prompt blocks the run, the watchdog kills the process after its silence budget, the card lands in `5-human-review` with a missing-terminal-sentinel marker, and the operator has to triage from logs. We observed this pattern several times on 2026-05-12 and 2026-05-13 (cards `bug-fast-abort-on-environment-blocker-repeat-patterns`, `feature-codex-sentinel-and-no-shell-system-prompt-prefix`, `feature-unified-cli-output-rendering-…`).

The platform's mitigation: spawn every CLI in its **maximum-permission / no-prompt mode** ("YOLO"). The trust boundary is moved *up* — from the CLI's interactive approval gate to the runner's task scope, project conventions, and post-run review. Evidence capture (logs, commits, run summaries, security review skill) is what makes this safe at scale, not per-command prompts.

| Decision | Default | Risk | Rationale |
|---|---|---|---|
| Permission mode | **YOLO / max** for all four CLIs | Medium-High | Non-interactive runner needs no approval gates; trust is enforced post-run via review lanes. |
| Override granularity | Per-project | Low | Sensitive projects can downgrade per agent without touching globals. |
| Auditability | All runs logged via Bus + git history | Low | Every command and every diff is captured and reviewable. |

## Per-CLI reference

### Claude Code (`claude`)

- **Knob**: `--dangerously-skip-permissions` (CLI flag) and `settings.json` `permissions.allow` patterns.
- **What "YOLO" means**: skip all per-tool approval prompts. Claude executes tool calls (Read, Bash, Edit, Write) without interrupting for confirmation.
- **Platform default**: `--dangerously-skip-permissions` for orchestrated runs.
- **Risk rating**: **Medium**. Claude has a strong agentic loop and self-corrects on errors; the watchdog catches stuck runs. The main risk is destructive Bash (`rm -rf`, `git reset --hard`); the platform forbids these via [docs/commit-push-doctrine.md](../commit-push-doctrine.md) and ADR-0019.
- **How to verify**: grep the spawn args in the run's `cli-output.log` for `--dangerously-skip-permissions`. Or call `GET /api/cli/claude/effective-mode?project=<name>` once that endpoint exists (see the [cli-config feature ticket](#related)).

### OpenAI Codex (`codex`)

- **Knob**: `sandbox_mode` + `approval_policy` in `~/.codex/config.toml` (top-level), plus `[windows] sandbox = "unelevated" | "elevated"`.
- **What "YOLO" means**:
  - `sandbox_mode = "danger-full-access"` — no per-command sandbox refusal.
  - `approval_policy = "never"` — no interactive approval prompts.
- **Platform default**: both set to the YOLO values above.
- **Risk rating**: **Medium-High**. Codex's sandbox is the most actively-blocking of the four on Windows; turning it off removes a real safety net. The platform compensates with watchdog timeouts and post-run review.
- **How to verify**: `Get-Content ~/.codex/config.toml | Select-String 'sandbox_mode|approval_policy'`. Also visible in `codex exec --json` output: when sandbox is on, you see `command_execution` items with `exit_code=null, status=in_progress` followed by a sandbox-refusal error; when YOLO, the command runs.
- **Quirk**: `[windows] sandbox` (`elevated`/`unelevated`) is a *separate axis* from `sandbox_mode`. We keep it on `unelevated` because `elevated` produces `CreateProcessAsUserW failed: 1312` on this machine (see comment in `~/.codex/config.toml`).

### GitHub Copilot CLI (`gh-copilot` / `copilot`)

- **Knob**: `--yolo` flag (Copilot-specific) or interactive `/yolo` slash command.
- **What "YOLO" means**: skip every per-action confirmation.
- **Platform default**: `--yolo` on the spawn command.
- **Risk rating**: **Medium**. Copilot's tool surface is smaller than Claude/Codex, so the blast radius per command is also smaller.
- **How to verify**: spawn command logged in `cli-output.log`. Once the cli-config feature ticket ships, `GET /api/cli/copilot/effective-mode?project=…`.

### Google Gemini CLI (`gemini`)

- **Knob**: `--skip-trust` (skip the workspace-trust prompt) and `-y` (auto-yes on subsequent prompts).
- **What "YOLO" means**: both flags set. The CLI does not pause for workspace trust or per-action confirmation.
- **Platform default**: `--skip-trust -y` on spawn.
- **Risk rating**: **Low-Medium**. Gemini's tool layer is more conservative by default; the prompts it skips are mostly noise for an automated runner.
- **How to verify**: spawn command logged. The Codex/Claude pattern of `effective-mode` endpoint applies once shipped.

## How to test on the fly

Per CLI, the simplest "is YOLO actually on?" probe:

```bash
# Codex
codex exec --json -- "node -e 'console.log(1+1)'"
# YOLO: prints {"type":"item.completed",...} with the result.
# Sandboxed: prints a sandbox-refusal error within ~10s.

# Claude
claude --dangerously-skip-permissions -p "Run `node -e 'console.log(1+1)'`"

# Copilot
gh copilot suggest --yolo "node -e 'console.log(1+1)'"

# Gemini
gemini --skip-trust -y -p "Run node -e 'console.log(1+1)'"
```

If the command runs without an interactive prompt and produces output, the agent is in YOLO. If it stalls > 10s waiting for user input, it is not.

## Override at the project level

The Project Settings → Agent Configuration surface lets you downgrade any agent for a specific project (e.g. `claude: workspace-write` for a security-sensitive repo). The toggle persists to the project's `cli-config.json` and takes effect on the next CLI spawn without a backend restart.

Inline-meta in the UI must reproduce:
1. The current mode + its source (project-override vs. global-default vs. CLI-default).
2. The one-paragraph "what / why / risk" from the matching subsection above.
3. The verification command from the previous section.
4. A drill-down link to this doc for the long form.

## Why this doc lives next to the controls

Per [design-principles.md §Inline meta](../design-principles.md#inline-meta-explain-decisions-next-to-the-lever): a setting without inline meta is a regression. The Markdown source is *here* (single source of truth); the UI embeds the relevant subsection inline. When this doc changes, the UI updates without copy-paste drift.

## Related

- [docs/design-principles.md §Inline meta](../design-principles.md#inline-meta-explain-decisions-next-to-the-lever) — the principle this doc is an example of.
- `feature-projekt-cli-konfiguration-mit-yolo-default-fuer-claudecodexcopilotgemini-doku-test-pfad` — implementation ticket for the Project Settings surface + the `effective-mode` probe endpoint.
- [docs/cli-skills/cli-claude.md](cli-claude.md), [cli-codex.md](cli-codex.md), [cli-copilot.md](cli-copilot.md), [cli-gemini.md](cli-gemini.md) — per-CLI operational reference.
- [docs/commit-push-doctrine.md](../commit-push-doctrine.md) — the post-run safety net that makes YOLO defensible.

## Open follow-ups

- The current `~/.codex/config.toml` change (2026-05-13) is a global stop-gap. Once the per-project CLI-config surface ships, that top-level `sandbox_mode = "danger-full-access"` can move into the project default and the stop-gap can be removed.
- `effective-mode` probe endpoint not yet implemented — the verification commands above are the manual fallback.

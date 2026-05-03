# Protocol & Activity Log Style Guide

Single source of truth for **what a job's protocol looks like** and **how images flow through it**. Read this before changing anything that touches `status.md`, the Activity Log, the protocol pane, or screenshots.

> **Language:** English. See [AGENTS.md](../AGENTS.md#documentation-language).

## 1. Two artefacts, two audiences

A finished job exposes two views of what happened:

| Artefact | Audience | Generator | File |
|----------|----------|-----------|------|
| **Activity Log** | Live observer / debugger | Streamed by the CLI driver, parsed by the frontend | `logs/cli-output.log` (raw) |
| **Protocol** | Reviewer ("what did the agent do?") | Haiku one-shot summary of the log tail | `status.md` |

Keep the boundary clean:

- The Activity Log is **mechanical**. Every tool call, every command, every diff snippet. Do not curate it.
- The Protocol is **editorial**. 5 to 10 bullet points the reviewer can scan in 30 seconds. It is regenerated from the log on demand and overwritten on the next run, so do not hand-edit it.

> ⚠ Agents must **never write `status.md` themselves.** It is owned by [`SummaryGenerationService`](../backend/Services/SummaryGenerationService.cs) and rewritten on each run from the CLI output. Anything written by hand is lost.

---

## 2. The Activity Log (`logs/cli-output.log`)

### 2.1 Marker-line format

The frontend's [`activity-log.parser`](../frontend/src/app/components/activity-log.parser.ts) classifies entries by leading marker. Drivers translate native CLI output into these markers in `TransformReadLine`:

| Marker | Kind | Example |
|--------|------|---------|
| `● Read /path` | Read | `● Read /src/foo.ts` |
| `● Edit /path` | Edit | `● Edit /src/foo.ts` |
| `● Run <cmd>` | Run | `` ● Run `npm test` `` |
| `● Search <pattern>` | Search | `● Search "TODO"` |
| `● Todo <text>` | Todo | `● Todo Refactor parser` |
| `● Task <text>` | Task | `● Task Investigate flaky spec` |
| Everything else | Message | freeform model output |

Rules:

- ANSI escapes are stripped on write. The base class does this; do not re-add them.
- UTF-8 only. The base class forces UTF-8 stdout/stderr; do not override.
- Streamed line-by-line, never buffered until the run finishes.

See [docs/supported-clis.md §2.5](supported-clis.md) for the per-CLI translation rules.

### 2.2 What the log can reference

Lines may contain:

- Absolute project paths, for example `C:\Projects\...\frontend\src\foo.ts`. The parser leaves these inline.
- Relative paths from the job folder, for example `attachments/abc.png`, `results/foo.png`. These are how images travel from the log into the protocol.
- Inline code spans in single backticks. Avoid triple backticks in marker lines.

---

## 3. The Protocol (`status.md`)

### 3.1 Canonical structure

[`prompts/runtime/summary-protocol.md`](../prompts/runtime/summary-protocol.md) is rendered by `SummaryGenerationService` after a successful CLI completion and instructs Haiku to emit exactly this English shape:

```markdown
# Status

- Result: <Success|Partial|Failed>
- Duration: <for example, 4 min>

## What Was Done
- 3 to 7 concrete bullets with actions, files, commands, and results.

## Open Items
- 0 to 5 bullets, or "None."

## Notes
- 0 to 3 bullets with warnings, failures, or workarounds. Omit this section when empty.

## Images
- ![](results/<name>.png) or ![](attachments/<name>.png). Omit this section when no images appear in the log.
```

Hard rules:

- No `# Status` is omitted. No extra `H1`s are added.
- Total prose is at most 250 words. Images do not count.
- Paths and commands in single backticks.
- No marketing tone, no recap of what the user already asked for.
- No em dashes.

If you change the prompt, mirror the change here and bump the example.

### 3.2 Why it's regenerated, not hand-written

- The reviewer always sees a fresh summary of the **most recent** run, not stale text from a previous attempt.
- The "Regenerate" button in the protocol pane re-runs Haiku against the same `cli-output.log`. This is useful when the first summary missed a detail.
- This means hand-writing into `status.md` is destructive: the next regen erases it. The model name for this rule is "the log is the truth, the protocol is the projection."

---

## 4. Image flow

Two folders, two purposes. Keep them separate:

| Folder | Direction | Lifetime | Used by |
|--------|-----------|----------|---------|
| `<job>/attachments/` | **Input.** Pasted/dropped into the prompt editor before the run. | Created lazily, persists with the job. | The CLI agent reads them via the relative path baked into `prompt.md`. |
| `<job>/results/` | **Output.** Screenshots produced *during* the run that prove the change. | Created on demand by the agent, persists with the job. | The protocol pane renders them; the reviewer reads them. |

Bare filenames in `status.md` (e.g. `![](foo.png)` with no folder prefix) are resolved against `results/` in the local reader as a fallback for older protocols. New work should always include the prefix.

### 4.1 Where images come from per CLI

| CLI | Image-producing capability | Default landing spot | Retention rule |
|-----|---------------------------|----------------------|----------------|
| **Claude Code** | None native. Produces images only by running tools (Playwright, custom scripts). | Wherever the tool writes them. | Agent must `cp`/`mv` into `<job>/results/` before declaring done. Otherwise lost on next Playwright run. |
| **Codex CLI** | Same as Claude. No native screenshot capability. | Tool-driven. | Same. Copy into `<job>/results/`. |
| **GitHub Copilot CLI** | Same. No native screenshot capability. | Tool-driven. | Same. Copy into `<job>/results/`. |
| **Gemini CLI** | Same. No native screenshot capability. | Tool-driven. | Same. Copy into `<job>/results/`. |
| **Playwright** (driven by any CLI above) | Test artifacts (screenshots, videos, traces) via `page.screenshot()`, etc. Project-wide default `outputDir: 'test-results'`. **Auto-harvested by `JobArtifactReporter` when `JOB_RESULTS_DIR` env var is set.** | When running under the agent task orchestrator: `<job>/results/playwright/<spec-name>/...` with `index.json` summary. Local dev (no env var): `frontend/e2e/test-results/<spec>/...` (ephemeral). | **Under orchestrator:** persistent, auto-copied with summary index. Protocol pane renders these images inline. **Local dev:** ephemeral scratch, manually copy if needed for review. Never reference `test-results/<...>.png` from durable `status.md`; use `results/` paths. |

**The retention rule, restated:** If a screenshot matters for the protocol, it must end up under `<job>/results/`. When running under the orchestrator, `JobArtifactReporter` copies Playwright artifacts automatically; otherwise manually `cp`/`mv`. Anywhere else is treated as scratch and may disappear on the next test run, the next CI cleanup, or a `git clean`. Do not reference `test-results/<...>.png` from `status.md`; it works locally for ten minutes and breaks for the reviewer.

### 4.2 Local rendering

The protocol pane renders `status.md` through [`markdownToHtml`](../frontend/src/app/components/markdown-utils.ts) with a `resolveImageSrc` that maps:

| Markdown source | Resolved URL |
|-----------------|--------------|
| `attachments/<name>` | `/api/jobs/{jobId}/attachments/{name}?watchPath=…` |
| `results/<name>` | `/api/jobs/{jobId}/results/{name}?watchPath=…` |
| `<name>.png` (no prefix) | `/api/jobs/{jobId}/results/{name}?watchPath=…` (fallback for legacy protocols) |
| Absolute `http(s)://…` | passed through unchanged |

The backend endpoints serve only files whose names contain no path separators and live directly under `attachments/` or `results/`. They reject `..`, `/`, and `\`; see [`JobScannerService.ResolveAttachment`](../backend/Services/Jobs/JobScannerService.cs) and the `results/` mirror.

### 4.2.5 Playwright artifact harvesting

When the agent task orchestrator runs a CLI (Claude Code, Codex, Copilot, Gemini), it sets the `JOB_RESULTS_DIR` environment variable to `<job>/results`. The `JobArtifactReporter` custom Playwright reporter (wired in `frontend/playwright.config.ts`) monitors this env var:

- **If set (orchestrator mode):** copies all test artifacts (screenshots, videos, traces) from `frontend/e2e/test-results/<spec>/...` into `<job>/results/playwright/<spec>/...`, preserving the subfolder structure. Writes `<job>/results/playwright/index.json` with a summary listing test status and artifact paths.
- **If unset (local dev):** reporter is silent; Playwright artifacts stay in the ephemeral `test-results/` folder as usual.

The frontend's markdown renderer and protocol pane already handle `results/playwright/<spec>/<name>` paths just like any other `results/` image. Haiku's summary (`status.md`) extracts image references from the CLI output; if the run produced screenshots and the CLI mentioned them, Haiku includes them in the `## Images` section of the protocol.

### 4.3 Git policy

Job folders are checked into the **target project's** repo (the watched workspace), not this app's repo. The product position is: **the protocol is the durable record; the screenshots are local proof.** Logs are text and cheap, so they push; images are heavy binaries and stay local.

Recommended `.gitignore` for the watched workspace. Match whichever job layout is in use:

```gitignore
# Canonical layout (filesystem-contract.md).
.orchestrator/jobs/**/attachments/
.orchestrator/jobs/**/results/

# Flat per-state layout (e.g. projects/<project>/<state>/<job>/).
**/attachments/
**/results/
```

The `**/attachments/` and `**/results/` patterns are broad on purpose: any folder named `attachments/` or `results/` anywhere in the workspace is treated as job-local image scratch. Adopt the canonical layout if that is too broad for your repo. Logs (`logs/`) are intentionally **not** ignored; they are the audit trail.

Already-committed images are not retroactively untracked by adding these rules. If a workspace has historical images in git, run `git rm --cached <path>` once to stop tracking them; the files stay on disk.

---

## 5. Changing this contract

Before you touch any of the moving parts:

1. If you change the **Haiku prompt** in `prompts/runtime/summary-protocol.md`, update section 3.1 in this file in the same PR.
2. If you change the **marker-line vocabulary**, update §2.1 here and the corresponding row in [docs/supported-clis.md §2.5](supported-clis.md).
3. If you add a new **image folder** convention, add a row to §4 and a resolver branch in `protocol-pane.component.ts`.
4. If you add a new **CLI**, fill in its row in §4.1 with observed behaviour, not assumptions.

The single-source-of-truth rule from [AGENTS.md](../AGENTS.md): if the doc and the code disagree, the doc is wrong. Fix it.

# Protocol & Activity Log Style Guide

Single source of truth for **what a job's protocol looks like** and **how images flow through it**. Read this before changing anything that touches `status.md`, the Activity Log, the protocol pane, or screenshots.

> **Language:** English. See [AGENTS.md](../AGENTS.md#documentation-language).

---

## 1. Two artefacts, two audiences

A finished job exposes two views of what happened:

| Artefact | Audience | Generator | File |
|----------|----------|-----------|------|
| **Activity Log** | Live observer / debugger | Streamed by the CLI driver, parsed by the frontend | `logs/cli-output.log` (raw) |
| **Protocol** | Reviewer ("what did the agent do?") | Haiku one-shot summary of the log tail | `status.md` |

Keep the boundary clean:

- The Activity Log is **mechanical** — every tool call, every command, every diff snippet. Don't curate it.
- The Protocol is **editorial** — 5–10 bullet points the reviewer can scan in 30 seconds. It is regenerated from the log on demand and overwritten on the next run, so do not hand-edit it.

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

- ANSI escapes are stripped on write (the base class does this — don't re-add them).
- UTF-8 only. The base class forces UTF-8 stdout/stderr; do not override.
- Streamed line-by-line, never buffered until the run finishes.

See [docs/supported-clis.md §2.5](supported-clis.md) for the per-CLI translation rules.

### 2.2 What the log can reference

Lines may contain:

- Absolute project paths (e.g. `C:\Projects\…\frontend\src\foo.ts`) — the parser leaves these inline.
- Relative paths from the job folder (e.g. `attachments/abc.png`, `results/foo.png`) — these are how images travel from the log into the protocol.
- Inline code spans in single backticks. Avoid triple backticks in marker lines.

---

## 3. The Protocol (`status.md`)

### 3.1 Canonical structure

`SummaryGenerationService` instructs Haiku to emit exactly this shape (German, by product decision):

```markdown
# Status

- Ergebnis: <Erfolg|Teilweise|Fehlgeschlagen>
- Dauer: <z. B. 4 min>

## Was wurde gemacht
- 3–7 Bullet-Punkte mit konkreten Aktionen (Dateien, Befehle, Ergebnisse).

## Offene Punkte
- 0–5 Bullet-Punkte oder „Keine".

## Auffälligkeiten
- 0–3 Bullet-Punkte mit Warnungen, Fehlern, Workarounds; sonst weglassen.

## Bilder
- ![](results/<name>.png) oder ![](attachments/<name>.png) — Sektion entfällt, wenn keine Bilder im Log auftauchen.
```

Hard rules:

- No `# Status` is omitted; no extra `H1`s are added.
- Total prose ≤ 250 words. Images don't count.
- Paths and commands in single backticks.
- No marketing tone, no recap of what the user already asked for.

If you change the prompt, mirror the change here and bump the example.

### 3.2 Why it's regenerated, not hand-written

- The reviewer always sees a fresh summary of the **most recent** run, not stale text from a previous attempt.
- The "Neu generieren" button in the protocol pane re-runs Haiku against the same `cli-output.log` — useful when the first summary missed a detail.
- This means hand-writing into `status.md` is destructive: the next regen erases it. The model name for this rule is "the log is the truth, the protocol is the projection."

---

## 4. Image flow

Two folders, two purposes — keep them separate:

| Folder | Direction | Lifetime | Used by |
|--------|-----------|----------|---------|
| `<job>/attachments/` | **Input.** Pasted/dropped into the prompt editor before the run. | Created lazily, persists with the job. | The CLI agent reads them via the relative path baked into `prompt.md`. |
| `<job>/results/` | **Output.** Screenshots produced *during* the run that prove the change. | Created on demand by the agent, persists with the job. | The protocol pane renders them; the reviewer reads them. |

Bare filenames in `status.md` (e.g. `![](foo.png)` with no folder prefix) are resolved against `results/` in the local reader as a fallback for older protocols. New work should always include the prefix.

### 4.1 Where images come from per CLI

| CLI | Image-producing capability | Default landing spot | Retention rule |
|-----|---------------------------|----------------------|----------------|
| **Claude Code** | None native. Produces images only by running tools (Playwright, custom scripts). | Wherever the tool writes them. | Agent must `cp`/`mv` into `<job>/results/` before declaring done. Otherwise lost on next Playwright run. |
| **Codex CLI** | Same as Claude — no native screenshot capability. | Tool-driven. | Same. Copy into `<job>/results/`. |
| **GitHub Copilot CLI** | Same — no native screenshot. | Tool-driven. | Same. Copy into `<job>/results/`. |
| **Gemini CLI** | Same — no native screenshot. | Tool-driven. | Same. Copy into `<job>/results/`. |
| **Playwright** (driven by any CLI above) | `page.screenshot()` writes wherever the spec says. Project-wide default `outputDir: 'test-results'`. | `frontend/e2e/test-results/<spec>/<artifact>.png`, **always overwritten** on the next run. | **Ephemeral.** Anything worth keeping must be copied into `<job>/results/` in the same task. The `test-results/` folder is `.gitignore`d and treated as scratch. |

**The retention rule, restated:** if the screenshot matters for the protocol, it must end up under `<job>/results/`. Anywhere else is treated as scratch and may disappear on the next test run, the next CI cleanup, or a `git clean`. Don't reference `test-results/<…>.png` from `status.md` — it works locally for ten minutes and breaks for the reviewer.

### 4.2 Local rendering

The protocol pane renders `status.md` through [`markdownToHtml`](../frontend/src/app/components/markdown-utils.ts) with a `resolveImageSrc` that maps:

| Markdown source | Resolved URL |
|-----------------|--------------|
| `attachments/<name>` | `/api/jobs/{jobId}/attachments/{name}?watchPath=…` |
| `results/<name>` | `/api/jobs/{jobId}/results/{name}?watchPath=…` |
| `<name>.png` (no prefix) | `/api/jobs/{jobId}/results/{name}?watchPath=…` (fallback for legacy protocols) |
| Absolute `http(s)://…` | passed through unchanged |

The backend endpoints serve only files whose names contain no path separators and live directly under `attachments/` or `results/`. They reject `..`, `/`, and `\` — see [`JobScannerService.ResolveAttachment`](../backend/Services/JobScannerService.cs) and the `results/` mirror.

### 4.3 Git policy

Job folders are checked into the **target project's** repo (the watched workspace), not this app's repo. Recommended `.gitignore` for the watched workspace:

```gitignore
# Keep the audit trail (text), drop the heavy artefacts (binaries).
.orchestrator/jobs/**/results/
.orchestrator/jobs/**/attachments/
.orchestrator/jobs/**/logs/
```

Logs may be re-included if their text-only nature outweighs the size; images stay out by default. The product position is: **the protocol is the durable record; the screenshots are local proof.** This is intentional and the user has signed off — see the job folder for the original prompt.

---

## 5. Changing this contract

Before you touch any of the moving parts:

1. If you change the **Haiku prompt** in `SummaryGenerationService.BuildPrompt`, update §3.1 in this file in the same PR.
2. If you change the **marker-line vocabulary**, update §2.1 here and the corresponding row in [docs/supported-clis.md §2.5](supported-clis.md).
3. If you add a new **image folder** convention, add a row to §4 and a resolver branch in `protocol-pane.component.ts`.
4. If you add a new **CLI**, fill in its row in §4.1 with observed behaviour, not assumptions.

The single-source-of-truth rule from [AGENTS.md](../AGENTS.md): if the doc and the code disagree, the doc is wrong — fix it.

# Orchestrator — Autopilot Workflow

This project is monitored by the Agent-Taskboard. Job folders live under `.orchestrator/jobs/` in numbered state folders.

## CRITICAL: Task Source

`.orchestrator/jobs/` is the **ONLY** source for tasks. Do NOT invent tasks, do NOT pick up work from other folders, issues, TODOs in code, or any other source. If `.orchestrator/jobs/2-ready/` is empty, **STOP**. Do nothing else.

## State Folders

```
.orchestrator/jobs/
  1-preparation/   ← Jobs being defined (human creates these)
  2-ready/         ← Ready for agent pickup
  3-progress/      ← Agent is working on it
  4-review/        ← Done, awaiting human review
  5-completed/     ← Reviewed and accepted
```

## Autopilot Loop

Follow this loop **strictly one job at a time, start to finish**:

1. **Scan** `.orchestrator/jobs/2-ready/` — this is the ONLY folder you pick work from
2. **Pick ONE job** — the one with the lowest `order` in `job.json`. If `2-ready/` is empty → **STOP completely**
3. **Move** the job folder from `2-ready/` into `3-progress/` (physically move the directory)
4. **Update** `job.json` → set `"state": "3-progress"`
5. **Read** `prompt.md` inside the job folder — this is your task description. Only NOW do you look at the task.
6. **Execute** the task described in `prompt.md` — work in the **project source tree** (src/, lib/, etc.), NOT inside the job folder
7. **Write the completion report** into the job's `status.md` (see format below) — this is the RESULT of the job
8. **If UI changes**: save Playwright screenshots into the job's `results/` folder, link them in `status.md`
9. **Move** the job folder from `3-progress/` into `4-review/`
10. **Update** `job.json` → set `"state": "4-review"`
11. **Repeat** from step 1. If `2-ready/` is empty → **STOP**

### CRITICAL: Sequential Processing

- **ONE job at a time.** Never pick up a second job while the first is still in `3-progress/`.
- Complete the full cycle (steps 1–10) before scanning for the next job.
- The `order` field in `job.json` determines pickup order (lowest = first).

## Completion Report (MANDATORY)

The completion report is written into **the job's own `status.md`** file. This is the single place where results live.

**Concrete path example:** If the job folder is `.orchestrator/jobs/3-progress/my-task/`, then the report goes into:
```
.orchestrator/jobs/3-progress/my-task/status.md
```

NOT somewhere else. NOT in a separate folder. Into `status.md` INSIDE the job folder.

**Report format:**

```markdown
# <Job Title>
> Report vom YYYY-MM-DD

## Erledigt
- ✅ Konkrete Aktion 1
- ✅ Konkrete Aktion 2

## Offen
- ❌ Was nicht gemacht wurde — kurze Begründung
- ⚠️ Teilweise erledigt — was fehlt noch

(Abschnitt "Offen" weglassen wenn alles erledigt ist)

## Geänderte Dateien
- `path/to/file.ts` — was geändert wurde
- `path/to/other.ts` — was geändert wurde

## Ergebnis
1–2 Sätze: Was der User sehen wird / was erreicht wurde.

## Screenshots
![Beschreibung](results/screenshot-name.png)

(Abschnitt "Screenshots" nur bei visuellen Änderungen)
```

Keep it factual and concise. The reviewer opens `status.md` and sees exactly what happened.

### Screenshots (nur bei UI-Änderungen)

Wenn die Aufgabe visuelle Änderungen beinhaltet:
1. Erstelle `results/` im **Job-Ordner** (z.B. `.orchestrator/jobs/3-progress/my-task/results/`)
2. Speichere Playwright-Screenshots dort
3. Verlinke im Report: `![Beschreibung](results/dateiname.png)`

Nur bei Bedarf — nicht jeder Job braucht Screenshots. Bei UI/Styling/Layout sind sie Pflicht.

## Job File Contract

Jeder Job-Ordner enthält:

```
my-task/
  job.json       ← Metadaten (id, title, state, order, agent) — id und createdAt NIE ändern
  prompt.md      ← Aufgabenbeschreibung — NUR LESEN, nie verändern
  status.md      ← HIERHIN kommt der Completion Report (siehe Format oben)
  results/       ← Screenshots (optional, nur bei visuellen Änderungen)
  logs/          ← Build-Outputs etc. (optional)
```

**Der Report geht in `status.md`. Nirgendwo anders.**

## Rules

- **ONLY** pick jobs from `2-ready/` — ignore all other state folders
- Never modify `prompt.md`
- Write the completion report into the job's `status.md` BEFORE moving to `4-review/`
- Work in the project source tree, not inside the job folder
- Update `job.json` `"state"` to match the folder the job is in
- Screenshots go into `results/` inside the job folder, linked from `status.md`

## Shell policy — sh, not PowerShell

- **Never invoke PowerShell from the agent.** Background launches and PID tracking break, the agent waits for prompts that never arrive.
- Use bash / sh (Git Bash on Windows is fine). Prefer existing `.sh` entrypoints (e.g. `./api.sh`) over inline shell snippets.
- For Windows-specific binaries (`tasklist`, `taskkill`, `netstat`), call them directly from sh — do not wrap in `powershell -c`.
- For file creation, use plain `cat <<'EOF' > path` heredocs or the agent's Write tool. No `Out-File`, `Set-Content`, here-strings.
- If a build cannot run in the current environment, state the concrete reason in `status.md` and continue with static verification.

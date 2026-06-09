# ASS-1707 — Erzeugendes Modell pro Nachricht/Output im Chat/Activity-Log

## Anforderung
Im next-gen Chat/Activity-Log (`nextGenChat`) soll **pro Nachricht/Output** das
**erzeugende Modell** (z. B. `claude-opus-4-8`, `gpt-5-codex`, `claude-haiku-4-5`)
nahe der Uhrzeit, subtil/sekundär dargestellt werden. Korrekt **pro Output**
(nicht ein globales Task-Modell), auch bei **Modellwechseln innerhalb eines Tasks**.

## Was implementiert wurde

### Projektion (Datenfluss, switch-aware)
`conversation-projection.ts`
- Liest das Per-Run-Modell aus den `[taskboard] Started … model=X …` System-Marker-Zeilen
  (Helper `readTaskboardMarker`), hält es in `currentModel` und **aktualisiert es bei
  jedem neuen Marker** → Modellwechsel innerhalb eines Tasks werden korrekt verfolgt.
- Die Marker-Zeile selbst wird nach dem Auslesen verworfen (`continue`), erscheint also
  nicht als Body-Text.
- `currentModel` wird an die zum Zeitpunkt erzeugten Events gehängt:
  - Agent-Messages (`:agent`, `:agent-error`)
  - Tool-Bursts (`toMergedToolBurst`)
  - Run-Marker (`toRunMarker`)
- **Bewusste Abstinenz:** Orchestrator-Entscheidungen, User- und Supervisor-Zeilen
  setzen **kein** `model` (kein Fabrizieren — siehe Boundary unten).

### Darstellung (subtil, zentrale Tokens, text-only)
- `conversation-view.component` — Modell-Badge `.msg__model` direkt nach
  `<time class="msg__time">`, im Bubble-Header. Gating: `@if (row.model)`.
  - Gruppierung ist **modell-uniform**: `ensureGroup(actor, ts, model)` bricht eine
    Bubble auf, wenn sich das Modell ändert; der Header (mit Badge) wird via
    `lastModel`-Tracking erneut gezeigt, auch wenn der Actor gleich bleibt.
- `tool-burst-chip.component` — Modell-Chip `.burst__chip--model` in der collapsed
  Burst-Zeile, nach dem Duration-Chip. Gating: `@if (event().model)`.
- Styling nutzt `color-mix(... currentColor ...)`, `var(--font-mono)`, gedämpfte
  Deckkraft → konsistent mit dem bestehenden Timestamp/Session-Styling, rein textuell.
  `[appTooltip]="'Generating model for this output'"` als Hover-Hinweis.

## Daten-Verfügbarkeits-Boundary (warum nicht überall ein Modell steht)

Das Per-Output-Modell ist **nur dort attribuierbar, wo es im cli-output.log steht**:

| Output-Art              | Quelle des Modells                         | Im Chat sichtbar? |
|-------------------------|--------------------------------------------|-------------------|
| Agent-Messages (Core)   | `[taskboard] Started … model=` (cli-output)| **Ja** — pro Run, switch-aware |
| Tool-Runs / Bursts      | derselbe Run-Marker (`currentModel`)       | **Ja** |
| Orchestrator-Decisions  | `OrchestratorChatLog` → **kein** model-Feld| Nein (keine Daten) |
| Aspect-Reviews          | `RunViaOneShotAsync` → pipeline-execution.json / FileGenerationIndex / `aspect-*.md`, **nicht** cli-output.log | Nein (nicht im Chat-Stream) |
| User-Zeilen             | n/a (Mensch, kein Modell)                  | Nein (korrekt) |

Aspect-Reviews und Orchestrator-Entscheidungen tragen ihr Modell **nicht** in den
Stream ein, aus dem der Chat projiziert wird. Es wäre falsch, dort ein globales
Task-Modell zu raten — das verstieße gegen "korrekt pro Output (nicht global)".
Daher wird in diesen Zeilen **bewusst kein Badge** gezeigt. Das Anbinden dieser
Quellen wäre eine separate Backend-Aufgabe (Modell in den Orchestrator-/Aspect-Stream
schreiben), die über den Scope dieses FE-Tasks hinausgeht.

## Verifikation
- Unit-Tests grün: `ng test --include=src/app/components/chat/{conversation-projection,conversation-view/conversation-view.component,tool-burst-chip/tool-burst-chip.component}.spec.ts`
  → **70 passed (3 files)**. Neu u. a.:
  - Projektion: liest Per-Run-Modell + verwirft Marker-Zeile; Per-Output-Modell über
    Mid-Task-Switch (`['gpt-5-codex','claude-opus-4-7']`); kein Modell für
    Orchestrator/User; Modell am Tool-Burst.
  - View: Badge neben Timestamp; kein Badge ohne Modell; Bubble-Bruch bei Mid-Task-Switch
    (2 Bubbles, 2 Badges); gleicher Actor+gleiches Modell → eine Bubble, ein Badge.
  - Tool-Burst-Chip: Modell-Chip in collapsed Row; kein Chip ohne Modell.
- Produktions-Typecheck grün: `tsc -p tsconfig.app.json --noEmit` → exit 0.

## Reissue-Verifikation (2026-06-09)

Auto-Review hatte den tests-and-evidence-Gate mit *concerns* markiert: „Unit tests
cover data projection but lack UI verification (screenshot or component test) that
model name displays next to timestamps." Daher hier die erneute, vollständige
Beweisführung — Build grün, Tests grün, **plus** Render-Pfad-Komponententests und
ein Screenshot.

### Build / Typecheck grün
- Produktions-Typecheck: `tsc -p tsconfig.app.json --noEmit` → **exit 0**.
- Spec-Build (`@angular/build:unit-test`): „Application bundle generation complete" →
  **NGTEST_EXIT=0**.

### Tests grün
- `ng test` über die drei betroffenen Specs → **Test Files 3 passed (3), Tests 70 passed (70)**.
- Davon decken die folgenden **Render-Pfad-Komponententests** (TestBed mountet die
  echte `app-conversation-view`, Assertions gegen das gerenderte DOM via
  `[data-testid="conversation-message-model"]`) explizit die UI-Darstellung ab —
  also genau das, was der Reviewer als fehlend bemängelt hatte:
  - `renders the generating model subtly next to the timestamp`
  - `omits the model badge when the output has no attributable model`
  - `breaks the bubble on a mid-task model switch so each bubble names one model`
  - `keeps same-actor same-model messages in one bubble with a single model badge`
  - (Tool-Burst-Chip, gerendert: `shows the generating model as a subtle chip on the
    collapsed burst row`, `omits the model chip when the burst has no attributable model`)

### Visueller Nachweis (Screenshot)
- `results/ui-evidence/model-badge-next-to-timestamp.png` — gerenderte Ansicht.
- Harness: `results/ui-evidence/model-badge-harness.html` — reproduziert die echte
  `app-conversation-view` + `app-tool-burst-chip` BEM-Struktur (1:1 aus den
  Component-Templates) mit den relevanten SCSS-Regeln **verbatim** aus
  `conversation-view.component.scss` / `tool-burst-chip.component.scss`; statisches,
  backend-freies Ziel für den Screenshot.
- Der Screenshot zeigt: einen **User**-Turn (kein Badge — Mensch), eine **Agent**-Bubble
  mit `gpt-5-codex` direkt neben der Zeit `12:00–12:02`, eine **Tools 3**-Burst-Zeile mit
  `gpt-5-codex`-Chip und eine **zweite Agent-Bubble** mit `claude-opus-4-8` neben `12:05`
  — d. h. **pro Output, switch-aware** (Modellwechsel mitten im Task), subtil neben der Uhrzeit.

## Geänderte Dateien
- `frontend/src/app/components/chat/conversation-event.ts` (model auf Base/RunMarker)
- `frontend/src/app/components/chat/conversation-projection.ts` (Marker-Lesen, currentModel)
- `frontend/src/app/components/chat/conversation-view/conversation-view.component.{ts,html,scss}`
- `frontend/src/app/components/chat/tool-burst-chip/tool-burst-chip.component.{html,scss}`
- Specs + Fixtures: `conversation-projection.spec.ts`, `conversation-projection.fixtures.ts`,
  `conversation-view.component.spec.ts`, `tool-burst-chip.component.spec.ts`

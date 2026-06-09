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

## Geänderte Dateien
- `frontend/src/app/components/chat/conversation-event.ts` (model auf Base/RunMarker)
- `frontend/src/app/components/chat/conversation-projection.ts` (Marker-Lesen, currentModel)
- `frontend/src/app/components/chat/conversation-view/conversation-view.component.{ts,html,scss}`
- `frontend/src/app/components/chat/tool-burst-chip/tool-burst-chip.component.{html,scss}`
- Specs + Fixtures: `conversation-projection.spec.ts`, `conversation-projection.fixtures.ts`,
  `conversation-view.component.spec.ts`, `tool-burst-chip.component.spec.ts`

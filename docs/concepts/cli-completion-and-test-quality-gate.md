# Konzept: Zuverlässige CLI-Completion-Erkennung + Test-Quality-Gate

> Status: **ENTWURF zur Review** (Operator-Entscheidung offen, v.a. beim Test-Gate).
> Kontext: Strategie-Eval 2026-06-09 — Kernursache der Churn = heuristisches Scrapen von CLI-stdout statt strukturierter Verträge. Dieses Dokument beantwortet: *Wann wissen wir zuverlässig, dass eine CLI fertig ist?* und *Was ersetzt das Sentinel-Scraping?*

## 1. Das Problem heute
- **Completion:** Wir erkennen „task done" über ein **Sentinel im stdout** (`[[TASK_DONE]]`) + Heuristiken. Für **Codex unzuverlässig** (silent-completion: der Agent ist fertig, sendet aber keinen Sentinel/`turn.completed` → Watchdog/Eskalation, oder „fertig aber nichts committet" wird fälschlich als Done akzeptiert). ~29 % der Eskalationen heute = missing-sentinel + classifier-unknown.
- **Test-Gate:** Der „BuildTestGate" **baut nur** (`dotnet build` + `npm run build`), **läuft keine Tests**. Er kann die ~27 vorbestehenden develop-Testfehler nicht von neuen unterscheiden → die Bürde landet bei einem **Haiku**-Reviewer, der aus einem Log-Tail rät → false reissue/block sauberer Arbeit.

## 2. Kernfrage: Wann wissen wir zuverlässig, dass die CLI fertig ist?
**Pro CLI unterschiedlich — und für BEIDE gibt es einen zuverlässigen Mechanismus. Wir nutzen ihn nur für Claude, nicht für Codex.** Deshalb sind wir „nur" bei einem Codex-Problem.

### Claude (Opus) — ZUVERLÄSSIG (deshalb stabil)
- Im headless-/`--output-format stream-json`-Modus emittiert Claude ein **finales `{"type":"result","subtype":"success"|"error_*",...}`-Event** und der **Prozess endet mit Exit-Code**. Dokumentierter, stabiler Vertrag.
- → **Completion = `result`-Event + Prozess-Exit.** Kein Raten. **Sicherheit: hoch.**

### Codex — heute UNZUVERLÄSSIG (das „Codex-Problem")
- Heute: `codex exec --experimental-json` + Sentinel-Scraping + `CodexSilentCompletionDetector` (Heuristik auf `command_execution`/`exit_code`-Frames).
- ABER: Codex **hat** ein strukturiertes Abschluss-Event — `turn_completed` (belegt in `cli-source-references/openai-codex/codex-rs`, u.a. als Notification). Im `exec`-stdout fehlt es jedoch teils (silent-finish); der **offizielle SDK-/App-Server-Pfad** liefert es zuverlässig + sauberen Prozess-Exit (ADR-0013).
- → **Fix: Codex-Completion über das SDK/App-Server-`turn_completed` + Prozess-Exit konsumieren**, nicht über stdout-Scraping. Dann ist „fertig" = Prozess sauber beendet, und „Done aber kein Commit auf einem Coding-Task" wird ein **harter NoOp/Fehler** statt akzeptiertem Done. **Sicherheit: der SDK-Pfad ist der richtige; das exakte Event ist am openai-codex-Source zu pinnen.**

### Prinzip (deine Vorgabe)
**Codex und Claude unterschiedlich behandeln** — jede CLI über IHREN strukturierten Vertrag, nicht über generisches stdout-Scraping. Das ist die *strukturelle* Lösung statt weiterer Symptom-Patches.

## 3. Test-Quality-Gate (hier bist du unsicher — zu Recht)
**Idee:** echtes **Test-Delta-Gate** statt build-only + Haiku-Raten.
- **Bei Task-Start:** Baseline der Suite auf develop-HEAD erfassen (welche Tests failen schon — die ~27).
- **Bei Review:** Suite laufen lassen, **nur das DELTA** melden (neu-failende Tests).
- **Grünes Delta = objektiver Pass. Nicht-leeres Delta = objektiver Block.** Kollabiert das „pre-existing vs. regression"-Raten, das requirement-fit/tests-Aspekte vergiftet, in ein deterministisches Ja/Nein.

**Offene Fragen (warum Vorsicht berechtigt ist):**
1. **Kosten/Zeit** — volle Suite pro Review ist teuer; viele Tests brauchen echte git/CLIs/Ports/CPU. Wie oft? Nur betroffene Tests? Nur an Lane-Übergängen (pre/post-merge) statt pro Tick?
2. **Flaky/Timing-Tests** verfälschen das Delta (false new-fail) → Quarantäne-Liste + Retry + stabile Baseline nötig.
3. **Baseline-Drift** — develop bewegt sich; wann wird die Baseline neu erfasst?
4. **Scope** — backend (`dotnet test`) ist machbar; frontend (Playwright-e2e) ist langsam/umgebungsabhängig.

**Empfehlung (klein anfangen):** backend `dotnet test` mit Baseline-Delta (die ~27 pinnen), frontend zunächst nur build. Flaky-Quarantäne pflegen. An Lane-Übergängen statt pro Tick. → **Deine Entscheidung, ob/wie wir das als Task umsetzen.**

## 4. Wie wir das nicht verlieren (Wiki)
Dieses Konzept lebt im **docs/-Wiki** und ist auf **Projekt-Ebene** abrufbar. Ein **Wiki-Post-Processing-Step** soll künftig Learnings automatisch hierher destillieren — Schluss mit klein-klein; Operator UND jede LLM-Instanz leiten Stand + Warum aus dem Wiki ab.

## 5. Status & nächste Schritte
- **Runner:** alles auf **Claude/Opus-4.8** umgestellt (Claude hat den zuverlässigen Vertrag) — Pipeline + Parallelität laufen weiter.
- **Tasks angelegt:** Wiki-Post-Processing-Step · Projekt-Wiki-Ansicht · Codex-SDK-Completion (ADR-0013) · Großes-Aufräumen/Wissens-System.
- **Test-Quality-Gate = dieses Dokument** — bewusst KEIN Task, bis du den Ansatz (insb. die 4 offenen Fragen) abgesegnet hast.

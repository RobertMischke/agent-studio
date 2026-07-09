# Run liveness & slot semantics — "in progress" must mean alive

**Status:** concept 2026-07-09 (~05:40), from the operator's night-shift
complaint: *"Tasks landen in Progress, kommen in den Auto-Loop, ein anderer
Task wird gezogen, und die liegen da rum."* Companion to
[`post-processing-immediacy-and-parallelism.md`](post-processing-immediacy-and-parallelism.md)
(§2.4/2.5) and the AGT-1944 outcome taxonomy; work-loss protection is solved
(AGT-1945), this concept fixes the *state* semantics.

## 1. The observed failure shape (one night, many costumes)

- A run enters the **auto-loop / steer** machinery (continue prompts, steer
  questions) and waits — while the admission logic pulls the next card.
  Result: 5–8 cards in 3-progress, only ~3 live CLIs, the rest in limbo.
- **Steer questions in unattended runs wait forever** (AGT-1936 deadlock:
  "provide the task description" asked into the void).
- **Process death leaves the lane untouched** (backend restarts → zombie
  3-progress; the supervisor meta-cycle noticed, but its remedy was pausing
  the whole project).

## 2. The invariant

> **3-progress bedeutet: Es existiert JETZT ein lebender, arbeitender
> Prozess für diese Karte.** Alles andere ist ein anderer, sichtbarer
> Zustand — niemals stilles Herumliegen.

## 3. Rules

1. **Loop-/Steer-Wartezeiten sind eigene sichtbare Sub-Zustände**
   (`loop-waiting`, `steer-pending`) — auf Karte und Board erkennbar (Phase
   pill), nicht als normales Progress getarnt. Lifecycle-Phasen existieren
   bereits (execution-running/-stalled …) — sie werden erweitert und in der
   Slot-Logik ausgewertet statt nur angezeigt.
2. **Unattended Steer hat einen Timeout mit Fallback.** Wenn niemand die
   Steer-Frage beantwortet (unbeaufsichtigter Betrieb), gilt nach T (Default
   120 s): auto-answer aus prompt.md/Task-Kontext, wenn eindeutig; sonst
   Routing per Outcome-Taxonomie (AGT-1944): `blocked` mit klarem Grund.
   Nie unbegrenzt warten.
3. **Slot-Belegung folgt Prozess-Leben, nicht Lane.** Eine Karte in
   `loop-waiting`/`steer-pending` oder im Post-Processing hält KEINEN
   Execution-Slot; beim Fortsetzen wird der Slot neu erworben (oder sichtbar
   gewartet). Admission zählt lebende Prozesse, nicht Lane-Mitgliedschaft.
4. **Liveness-Heartbeat + automatische Demotion.** Jede 3-progress-Karte
   hat einen Run-Heartbeat. Stirbt der Prozess (Crash, Backend-Neustart,
   Kill), demotet der Runner die Karte selbsttätig binnen ≤60 s nach
   2-ready mit Grund `process-lost` (Session-Resume-Zeiger wird dabei
   geleert → kein Launch-Fail-Folgetod). Kein Zombie überlebt eine Minute;
   kein Meta-Cycle und keine Projekt-Pause nötig.
5. **Arbeit ist dabei immer sicher** — Demotion/Cancel erst nach
   Sicherungs-Commit-Pfad (AGT-1945-Invariante, deployed).

## 4. Implementation cut

| Slice | Scope |
|---|---|
| A | Heartbeat + `process-lost`-Demotion (Regel 4) — beseitigt Zombies strukturell; ersetzt den Meta-Cycle-Anwendungsfall "stuck-in-progress" |
| B | Steer-Timeout + Fallback (Regel 2) — beseitigt den 1936-Deadlock |
| C | Sub-Zustände sichtbar + Slot-Accounting auf Prozess-Leben (Regeln 1+3) — beseitigt "liegt rum, während andere gezogen werden" |

A ist unabhängig und zuerst; B klein; C berührt Admission/UI und kommt
zuletzt. Zusammen mit AGT-1944 (Routing) ergibt das: Jede Karte ist zu jedem
Zeitpunkt entweder *lebendig arbeitend*, *sichtbar wartend mit Grund und
Timeout*, oder *sauber zurücksortiert*.

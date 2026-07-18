# Konsolidierungsanalyse Agent-Studio-Wiki — 2026-07-18

Analyse-Snapshot über den kompletten `docs/`-Baum (Worktree `wiki-main`, Branch `main`)
plus das Wissen außerhalb des Wikis (`artifacts/improvement-plans/`).
Zweck: Grundlage für Roberts Aufräum- und Kurationspass. Diese Seite verändert nichts —
sie inventarisiert, gruppiert und empfiehlt.

Methodik: Jede der 474 Seiten wurde mindestens mit Titel + erster Sektion erfasst
(Digest-Extraktion), unklare Fälle vollständig gelesen. Letzte Änderungsdaten stammen aus
`git log` (ein Pass über die gesamte Historie, neuester Commit pro Pfad).

---

## Zusammenfassung

- **474 Seiten** (412 md, 62 html) in 21 Top-Ordnern; dazu 49 `.meta.json`-Sidecars, 17 generierte `.report.html`-Companions, 12 JSON-Schemas, Bild-Assets. Zeitspanne der letzten Änderungen: **2026-05-03 bis 2026-07-18**.
- Davon **generiert/uniform**: 110 common-problems-Dateien (18 Probleme × 6), 66 Survey-Proposals (paarweise Duplikate!), 13 Lane-Guides, 11 Visual-Feature-Seiten, 6 Workstream-Landing-Pages. Die eigentliche "Kurationsmasse" sind **~180 Hand-Seiten**.
- **Größtes Problem 1 — Generationen-Stapel Remote/Runner/Completion:** mindestens 4 Generationen von Stabilitäts-/Completion-Analysen liegen nebeneinander (Mai-Postmortems → Juni-Incident-Seiten → Juli-Umbrella `completion-review-…` → **Härtungs-Workbench 18.07. = neuer kanonischer Ort**). Die Juni-HTMLs in `docs/wiki/concepts/` tragen keine "überholt durch"-Banner.
- **Größtes Problem 2 — Selbstreferenz-Wildwuchs "Wiki über das Wiki":** 10+ Seiten (wiki-tree, Pulse, Grading, Classification, Editing-Flow, docs-structure-migration, meta/*, usage) ohne eine klare Einstiegsseite; `docs/meta/documents/` + `docs/meta/reports/documents/` sind laut eigenem Drift-Audit **Alt-Evidenz vor der Sidecar-Migration**.
- **Größtes Problem 3 — Sprachmix gegen die eigene Policy:** AGENTS.md verlangt Englisch, aber der neue kanonische Härtungs-Ort, 2 Konzepte und 2 Mockups sind Deutsch; der Drift-Report zu `remote-hosts-ux.html` moniert genau das (D5).
- **Größtes Problem 4 — Wissen außerhalb des Wikis:** Phantom-Welle-/Salvage-Vollzugsprotokoll (17.07.), Org-Migration agent-orc und Pipeline-UI-Handoffs liegen nur im Devspace und sind aus dem Wiki nicht erreichbar.
- **Top-5-Sofortmaßnahmen:** (1) "Überholt durch Härtungs-Workbench"-Banner auf `overnight-2026-06-23-summary.html`, `claude-termination-investigation.html`, `runner-stability-incidents.html`; (2) `docs/meta/documents/` + `meta/reports/documents/` archivieren/löschen; (3) Duplikat-Paare in `proposals/2026-07-11/` (33 inhaltsgleiche Paare) über die Hub-Löschfunktion halbieren; (4) `salvage-reconciliation-2026-07-17.md` + `github-org-naming-handoff-2026-07-11.md` ins Wiki übernehmen; (5) kuratierte Startseite aus den Kurations-Seeds unten bauen.

---

## Bestandsaufnahme

Legende Typ: K=Konzept, ADR, C=Contract, D=Domain-Map, A=Analyse/Research, R=Runbook/Ops, W=Workbench, P=Proposal, M=Mockup/Design-Spec, G=generiert, I=Index/README.

| Ordner | Seiten (md/html) | Dominante Typen | Sprache | Ältester … neuester Stand | Zustand |
|---|---|---|---|---|---|
| `docs/` (Root) | 1 (1/0) | I (Documentation Index) | en | 2026-07-14 | aktuell, sehr guter Einstieg |
| `architecture/` | 24 (17/7) | ADR, K, C, G (7 report.html) | en | 2026-06-11 … 2026-07-14 | Kern aktuell; `bus/implementation-state.md` (Stand 05-11) veraltet |
| `cli/` | 10 (10/0) | C, Skill-Referenzen, A | en | 2026-06-11 … 2026-07-11 | aktuell; **`skills/cli-copilot.md` fehlt** (gelöscht, aber Copilot ist supported und wird 10× in `onboard-an-agent-cli.md` referenziert) |
| `concepts/` | 33 (26/7) | K + Konzept-Mockups | en, 2×de (`cli-completion…`, `git-branching…`), Mockups 2×de | 2026-06-09 … 2026-07-17 | Kern des Zielbilds; 5 Seiten tragen bereits korrekte historical/superseded-Banner |
| `contracts/` | 10 (10/0) | C | en | 2026-06-11 … 2026-07-13 | aktuell, kanonisch |
| `design/` | 5 (2/3) | Hard Rules + datierte HTML-Reports | en | 2026-07-11 … 2026-07-14 | aktuell (app-survey ist 19 MB Evidenz-Snapshot) |
| `domains/` | 9 (8/1) | D | en | 2026-06-11 … 2026-07-14 | aktuell; `cli.md` Version 06-09 = älteste Map |
| `engineering-workstream/` | 6 (0/6) | G (fixer Frame, 5 Areas) | en | 2026-07-10 … 2026-07-13 | aktuell, Infrastruktur — nicht anfassen |
| `frontend/` | 27 (26/1) | Design-System, Style-Guide, Audits | en | 2026-06-11 … 2026-07-14 | Kern aktuell; Audits datiert (05-09…06-09) |
| `in-app-help/` | 14 (14/0) | R (Lane-Guides, von App serviert) | en | 2026-06-11 | aktuell (Produkt liest sie); `lane-1a`/`lane-3a` = retired-Themen |
| `meta/` | 5 (3/2) + Alt-JSON | I, Reports | en | 2026-06-12 | tw. **historisch**: `documents/` + `reports/documents/` sind Vor-Sidecar-Alt-Evidenz |
| `mockups/` | 42 (31/11) | M (8 Familien) | en | 2026-05-05 … 2026-07-13 | 3 Familien aktuell (project-urls, project-overview-dashboard, task-processing-pipeline); Rest Mai-Generation = Design-Geschichte |
| `operations/` | 21 (21/0) | R | en | 2026-06-11 … 2026-07-13 | aktuell; Security-Seiten tragen korrekte "vor Remote neu schreiben"-Scope-Notes |
| `product/` | 10 (10/0) | K/Produkt | en | 2026-06-11 … 2026-07-11 | überwiegend aktuell; `companion-app-design.md` V1-Status unklar |
| `proposals/` | 67 (67/0) | P (generiert) | en | 2026-07-11 … 2026-07-13 | Generation 2026-07-11; **33 Duplikat-Paare** (gleicher Text, 2 Screenshots) |
| `quality/` | 4 (4/0) | Style-Guide-Familie (prompt-injected) | en | 2026-07-14 | aktuell, neuester kanonischer Einstieg für Engineering-Guides |
| `reports/` | 20 (17/3) | C (Report-Verträge), Visual-Doku | en, 1×de (`bus-architecture-report.html`) | 2026-06-11 … 2026-07-11 | Verträge aktuell; beide HTML-Reports Stand 05-11 = historisch |
| `research/` | 23 (23/0) | A (datiert) | en | 2026-05-03 … 2026-07-13 | bewusst historisches Archiv; 3 Seiten faktisch überholt (s.u.) |
| `schemas/` | 1 (1/0) + 12 JSON | C | en | 2026-07-12 | aktuell |
| `wiki/` | 138 (129/9) | common-problems (110), Konzept-/Wissensseiten (24), learnings, .drift | en | 2026-05-30 … 2026-07-14 | Kern aktuell; die Juni-Incident-HTMLs sind der größte "überholt"-Block |
| `workbenches/` | 4 (1/3) | W | de (`haertung…`), en (`architecture-quality-layer`) | 2026-07-14 … 2026-07-18 | **neuester kanonischer Härtungs-/Vorfalls-Ort** (18.07., aus `wiki/concepts/` hierher verschoben, Commit `18b62c8a`) |

Sidecar-/Companion-Infrastruktur (nicht kuratieren, aber wissen): `*.meta.json` (49) und
`*.report.html` (17, alle Stand 2026-06-12) liegen neben ihren Quelldokumenten;
`docs/wiki/.drift/` enthält nur 2 Producer-Notizen (Bus-Contract, Remote-Hosts-Mockup) —
inkonsistent dünn, aber harmlos.

### Detail: die entscheidungsrelevanten Einzelseiten

Nur Seiten, deren Zustand nicht "aktuell" oder trivial ist (vollständige uniforme Gruppen s.o.).

| Pfad | Titel/Kurzinhalt | Typ | Stand | Zustand |
|---|---|---|---|---|
| `wiki/concepts/completion-review-and-remote-runner-stability.html` | Umbrella: semantische Completion, Exact-Revision-Review, Runner-Provenienz, Remote-Stabilität | K/A | 07-13 | aktuell — kanonisches EN-Konzept der Kette; sollte auf Härtungs-Workbench verweisen |
| `wiki/concepts/runner-stability-incidents.html` | Incident-Log + Invarianten (seeded 06-23) | A | 07-13 | **überholt durch** `workbenches/haertung-verteilte-ausfuehrung/historie.html` (deren Pflege-Regel: "der eine Ort für Vorfälle") |
| `wiki/concepts/overnight-2026-06-23-summary.html` | Overnight-Session-Zwischenstand 06-23 | A | 06-23 | historischer Sessionbericht; überholt durch historie.html |
| `wiki/concepts/claude-termination-investigation.html` | claude.exe-Kill-Forensik, RESOLVED 06-23 (Sentinel-Scanner-Bug) | A | 06-23 | abgeschlossen; als Forensik-Archiv behalten, Banner setzen |
| `wiki/concepts/process-termination-scenarios.html` | Termination-/Abort-Szenario-Testsuite-Spec | C/A | 06-23 | inhaltlich noch nützlich (Szenario-Matrix), aber Pflege eingeschlafen; mit Gate-/Testsuite-Realität abgleichen |
| `wiki/concepts/orchestrator-drive-to-conclusion.html` | Drive-to-Conclusion, Slice 1 live 06-22 | K | 07-13 | aktuell (designated topic), Implementierungsstand prüfen |
| `wiki/concepts/orchestrator-supervision-loop.html` | Watch-Loop-Konzept (proposed) | K | 06-22 | aktuell als Konzept (designated topic) |
| `wiki/concepts/auto-review-evidence-gate-analysis.html` | 9-Task-Audit "Needs rework", Evidence-Gate | A | 07-13 | aktuell (designated topic); Überschneidung mit completion-review-Umbrella §6 |
| `wiki/concepts/task-integration-merge-config-analysis.html` | MaxParallelism wählt still 2 Git-Pipelines; Integration-Policy-Vorschlag | A | 06-22 | aktuell als Analyse; Ist-Zustand kanonisch in `task-integration-and-merge-workflow.md` |
| `wiki/concepts/docs-structure-migration.md` | Migrationsrekord der 06-11-Umstrukturierung | A | 06-11 | historisch (korrekt so deklariert) |
| `concepts/git-branching-integration-zielbild.md` | Git-Zielbild-Entwurf | K | 07-13 | **HISTORICAL DRAFT** (eigenes Banner) → `release-semantics.md` |
| `concepts/project-chartroom-concept.md` | Chartroom | K | 07-08 | **SUPERSEDED** (eigenes Banner) → `engineering-workstream.md` |
| `concepts/remote-execution-product-integration.md` | Remote im Produkt (Slices) | K | 07-13 | tw. historisch (eigenes Banner) → `distributed-agent-studio-target-architecture.md`; Host-Onboarding/UI-Slices weiter nützlich |
| `concepts/project-relationship-model.md` | Branch-aware Wiki, Projekt/Repo-Kardinalität | K | 07-13 | historisch (eigenes Banner) → distributed-…; Branch-Provenienz-Teil weiter nützlich |
| `concepts/task-execution-and-log-architecture.md` | Log-/Exec-Architektur | K | 07-13 | historisches Fundament (eigenes Banner) → distributed-… |
| `concepts/mockups/remote-hosts-ux.html` | 4 Remote-Hosts-Screens (deutsch) | M | 07-07 | **Drift-Warn 56/100** (`wiki/.drift/…`): 4-Schritte-Wizard statt 5 (Push-Key fehlt), kein Status-Banner, Sprache DE |
| `concepts/cli-completion-and-test-quality-gate.md` | Completion-Erkennung + Test-Gate (deutsch, ENTWURF 06-09) | K | 06-09 | vermutlich veraltet: Kernfragen inzwischen in Run-Outcome-Contract, completion-review-Umbrella und Gates-Realität beantwortet |
| `research/remote-ready-kickoff-2026-07.md` | Remote-Kickoff, Phasenplan | A | 07-13 | historischer Phasenplan (eigenes Banner) → distributed-… + linux-runner-host |
| `research/wsl2-vs-windows-decision-2026-05.md` | WSL2 vs Windows | A | 05-03 | überholt durch ADR-0059/Linux-Runner-Realität; Design-Geschichte |
| `research/project-chat-progress-indicator-2026-05-08.md` | Chat-Progress v1 | A | 06-11 | überholt durch `…-2026-06-08.md` (sagt es selbst) |
| `research/embedded-chat-integration-2026-05.md` | Chat-Mockup-Integration | A | 05-14 | historisch; Chat lebt inzwischen als `@coding-agent/chat`-Lib (Devspace-Memory) |
| `architecture/bus/implementation-state.md` | Bus-Implementierungsstand ("Living Doc") | A | 06-11 (Inhalt: 05-11) | veraltetes Living-Doc — aktualisieren oder als Snapshot deklarieren |
| `architecture/decisions/proposed/adr-0051-…` | Task-Pipeline-ADR (proposed seit 05-30) | ADR | 07-11 | weitgehend implementiert (Pipeline/Domain-Map existiert) → in adr-archive falten |
| `cli/investigations/codex-runner-investigation.md` | Codex NOOP-Bug, Fix 05-12 | A | 06-11 | abgeschlossen, Archiv |
| `reports/html/bus-architecture-report.html` | Bus-Report (deutsch, Stand 05-11) | A | 06-11 | historisch |
| `reports/html/orchestrator-system-visual-report.html` | Filesystem-Layer-Explorer | A/M | 06-11 | historisch/experimentell |
| `meta/reports/wiki-drift-audit-2026-06-11.html` | Drift-Audit über 348 Seiten | A | 06-12 | historischer Snapshot (eigenes Banner: vor Sidecar-Migration) |
| `product/companion-app-design.md` | Companion-App V1 | K | 06-11 | Status prüfen (PWA-Follow-up offen) |
| `frontend/audits/*` (5) | SCSS/Architektur/Tooltip/Selector-Audits | A | 06-11 | datierte Audits; `scss-quality.md` bleibt Playbook, `scss-quality-eval-2026-05-17.md` ist dessen Verlaufs-Snapshot |
| `mockups/chat-window-next-gen/` (14) | Chat-v7-Mockup-Familie (Mai) | M | 05-05…06-22 | Design-Geschichte der heutigen Chat-Richtung |
| `mockups/kanban-board-design/`, `vscode-layout/`, `orchestrator-meta-cycle/`, `orchestrator-prep-and-autonomy/`, `task-progress-tracking/`, `quality-system/` | Mai-Mockup-Familien | M | 05-05…06-22 | gelockte Specs bzw. Design-Geschichte; quality-system lebt weiter über `concepts/architecture-quality-layer.md` |
| `wiki/README.md` | Wiki-Einstieg + common-problems-Konventionen | I | 06-23 | aktuell, aber Intro-Absatz 3× fast wortgleich wiederholt — zusammenziehen |
| `wiki/learnings/README.md` | "No learnings distilled yet" | G | 06-09 | verwaist-leer (Step opt-in, nie gelaufen) |

---

## Redundanz-Cluster

### Cluster A — Remote/Runner-Stabilität, Completion, Härtung (4 Generationen)

Das mit Abstand größte Konsolidierungsfeld.

| Generation | Seiten | Empfehlung |
|---|---|---|
| Gen 1 (Mai-Postmortems) | `research/arhciv-loop-postmortem-2026-05.md`, `auto-pickup-cascade-analysis-2026-05.md`, `anthropic-5xx-frequency-2026-05-07.md`, `runner-outcome-visibility-2026-05-11.md`, `cli-orchestration-survey-2026-05.md`, `path-forward-plan-2026-05.md`, `wsl2-vs-windows-decision-2026-05.md` | behalten als Research-Archiv (datiert, korrekt einsortiert); wsl2 als "überholt durch ADR-0059" bannern |
| Gen 2 (Juni-Incident-Seiten) | `wiki/concepts/runner-stability-incidents.html`, `process-termination-scenarios.html`, `claude-termination-investigation.html`, `overnight-2026-06-23-summary.html`, `orchestrator-drive-to-conclusion.html` | **runner-stability-incidents, overnight, claude-termination: "überholt durch historie.html" bannern** (Chronik-Monopol liegt jetzt dort); process-termination-scenarios als Testsuite-Spec prüfen/behalten; drive-to-conclusion bleibt (designated topic) |
| Gen 3 (Juli-Umbrella) | `wiki/concepts/completion-review-and-remote-runner-stability.html` (112 KB, 07-13) | **kanonisch für Completion-/Review-/Provenienz-Semantik (EN)**; Querverweis auf Workbench ergänzen |
| Gen 4 (kanonischer Härtungs-Ort) | `workbenches/haertung-verteilte-ausfuehrung/index.html` + `historie.html` (18.07.) | **kanonisch für Vorfalls-Chronik + Härtungsprogramm**; einzige Pflegestelle für neue Vorfälle |

Dazu die Remote-**Zielbild**-Achse (kein Duplikat, aber ein Verweisnetz, das stimmen muss):
kanonisch = `concepts/distributed-agent-studio-target-architecture.md` (07-13);
historisch mit korrekten Bannern = `remote-execution-product-integration.md`, `research/remote-ready-kickoff-2026-07.md`;
Runbooks (aktuell, komplementär) = `operations/remote-hosts.md`, `operations/setup/linux-runner-host.md`, `operations/setup/remote-runner-persistent-connection.md`;
Mockup `concepts/mockups/remote-hosts-ux.html` = Drift-Warn, historisch bannern (Empfehlung des Drift-Reports übernehmen).
ADR-0060 (`architecture/decisions/proposed/`) ist der produktive Lease-Slice dieser Achse — offen halten.

### Cluster B — Task-Integration / Merge / Git-Branching

| Seite | Rolle | Empfehlung |
|---|---|---|
| `wiki/concepts/task-integration-and-merge-workflow.md` | Ist-Zustand (code-verifiziert) | **kanonisch (Ist)** |
| `concepts/release-semantics.md` | entschiedenes Modell 07-13 | **kanonisch (Entscheidung)** |
| `wiki/concepts/task-integration-merge-config-analysis.html` | Analyse + Integration-Policy-Vorschlag | behalten als Analyse, aus den beiden kanonischen verlinken |
| `concepts/parallel-task-execution.md` | Design-Home zu ADR-0052 | behalten (Design-Home) |
| `concepts/git-branching-integration-zielbild.md` | HISTORICAL DRAFT | Banner ok — kein Handlungsbedarf, ggf. nach `research/` verschieben |
| `operations/git/commit-push-doctrine.md` + `commit-attribution-discovery.md` | Doktrin + Audit | behalten, kanonisch für Commit-Grenze |

### Cluster C — URL/Preview ("Project URLs")

| Seite | Rolle | Empfehlung |
|---|---|---|
| `mockups/project-urls/README.md` + `ui.html` | Feature-Spec + Prototyp (07-10/13 aktualisiert) | **kanonisch** für Project-URLs |
| `mockups/project-overview-dashboard/README.md` + `ui.html` | Dashboard enthält Project-URLs-Sektion | behalten; auf project-urls verweisen statt Spec duplizieren |
| `proposals/2026-07-11/survey-…-032/033-project-urls.md` | generierte Survey-Findings | Duplikatpaar; über Hub-Statusverwaltung abwickeln |
| `concepts/deployment-first-class.md` | Deploy-Targets (verwandt, nicht gleich) | behalten; Grenze URLs↔Deploy-Targets in einem Satz je Seite klären |

### Cluster D — Wiki über das Wiki

| Seite | Rolle | Empfehlung |
|---|---|---|
| `contracts/wiki-tree.md` | physisches Baum-/Render-Contract | **kanonisch** |
| `concepts/wiki-pulse-dashboard.md` (+ Mockup) | Pulse (PULSE-1/2 implementiert) | kanonisch für den Einstiegs-View |
| `concepts/wiki-grading-run.md` | Grading (GRADE-1 implementiert) | kanonisch fürs Grading |
| `product/wiki-document-classification.md`, `product/wiki-editing-and-branch-flow.md` | Klassifikation, Editing | behalten |
| `wiki/concepts/docs-structure-migration.md` | Migrationsrekord 06-11 | historisch, ok |
| `meta/README.md` + `meta/reports/*` + `meta/usage/*` | Metadaten/Reports/Telemetry-Konzept | `meta/documents/` + `meta/reports/documents/` = Alt-Evidenz → archivieren/löschen; usage-Konzept behalten |
| `concepts/project-relationship-model.md` (+ Mockup) | branch-aware Wiki (historisch) | Banner ok |
| `wiki/README.md` | Einstieg + Problemkonventionen | Intro deduplizieren |

Empfehlung: eine einzige "Wiki-System"-Übersichtszeile in `docs/README.md` bzw. der neuen
kuratierten Startseite, die diese 10 Seiten in kanonisch/lebend/historisch ordnet.

### Cluster E — Orchestrator-Chat / Chat-Fenster

| Seite | Rolle | Empfehlung |
|---|---|---|
| `concepts/multichat-orchestrator.md` (+ de-Mockup) | kontextgebundene Sessions (AGT-1917) | **kanonisch** (aktuellste Richtung) |
| `concepts/orchestrator-in-app.md` | ORCH-Sight/Tools | kanonisch (Ergänzung) |
| `product/orchestrator-chat.md` + `orchestrator-chat-redesign-handoff.md` | persistenter Chat, Redesign-Brief | behalten; Verhältnis zu multichat in 1 Statuszeile klären (Redesign-Handoff ist die ältere Nordstern-Fassung) |
| `mockups/chat-window-next-gen/` (14 Dateien) | Mai-Mockup-Generation v7 | Design-Geschichte; Familien-README genügt als Einstieg, keine Einzelbanner nötig |
| `research/embedded-chat-integration-2026-05.md`, `project-chat-progress-indicator-2026-05-08.md` → `…-2026-06-08.md` | Integrations-/Progress-Research | 05-08-Version als "überholt durch 06-08" bannern |

### Cluster F — Orchestrator-Supervision / Meta-Loop / Lanes

- Kanonisch: `wiki/concepts/orchestrator-supervision-loop.html` (Konzept), `in-app-help/lane-guides/` (Ist-Verhalten, App-served), `domains/tasks.md`/`domains/runner.md` (System of record).
- Design-Geschichte: `research/orchestrator-meta-loop-analysis-2026-05-04.md`, `mockups/orchestrator-meta-cycle/`, `mockups/orchestrator-prep-and-autonomy/`, `research/expanded-lifecycle-lanes-plan-2026-05.md`, `research/escalated-lane-and-decision-surface-2026-06.md`, `research/orchestrator-prep-as-active-pipeline-step-2026-06.md`, `research/auto-review-postprocessing-consolidation-2026-06.md`, `research/orchestrator-decision-protocol-2026-05.md`.
- Konsistenz-Check nötig: `orchestrator-prep-as-active-pipeline-step` will die `1a`-Lane abschaffen — `lane-guides/lane-1a-orchestrator-prep.md` beschreibt sie als lebendig. Eine Seite von beiden braucht eine Statuszeile.

### Cluster G — Style-Guides (3 Familien, bereits gut verzahnt)

`quality/` (kanonischer Einstieg, prompt-injected) ⇄ `design/style-guide-hard-rules.md` (visuelle Hard Rules) ⇄ `frontend/style-guide/` (Komponenten-Vokabular) ⇄ `frontend/design-system.md` (Token-Contract). Keine Merges nötig — die Verweiskette existiert seit 07-14. Einzige Empfehlung: Audits (`frontend/audits/`, `frontend/style-guide/audit-*`) als "datiert" verstehen, nicht pflegen.

### Cluster H — Proposals-Duplikate

Die Generation `proposals/2026-07-11/` enthält 66 Dateien, davon **33 inhaltsgleiche Paare**
(gleiches Proposal, zwei Screenshot-Varianten, z. B. 004/005, 008/009, 014/015 …).
Empfehlung: künftige Generationen pro Finding EINE Datei mit N Screenshots; die bestehende
Generation über die Hub-Funktionen (reject/remove/delete-old-generations) ausdünnen, nicht per Hand im Baum.

---

## Veraltet / Überholt

Seiten, deren Inhalt von neueren Seiten oder vom Code überholt ist. "Banner fehlt" = Handlungsbedarf.

| Seite | Überholt durch | Banner? |
|---|---|---|
| `wiki/concepts/runner-stability-incidents.html` | `workbenches/haertung-verteilte-ausfuehrung/historie.html` (Chronik-Monopol) | **fehlt** |
| `wiki/concepts/overnight-2026-06-23-summary.html` | historie.html (Vorfälle dort aufgearbeitet) | **fehlt** |
| `wiki/concepts/claude-termination-investigation.html` | in sich RESOLVED; Chronik → historie.html | **fehlt** (nur "RESOLVED" im Text) |
| `concepts/cli-completion-and-test-quality-gate.md` (de, ENTWURF 06-09) | `contracts/run-outcome.md`, completion-review-Umbrella, Gates-Realität (Testsuite-Gates seit 07) | **fehlt** |
| `research/wsl2-vs-windows-decision-2026-05.md` | ADR-0059 + `operations/setup/linux-runner-host.md` | **fehlt** |
| `research/project-chat-progress-indicator-2026-05-08.md` | `…-2026-06-08.md` | indirekt (Nachfolger sagt es) |
| `concepts/mockups/remote-hosts-ux.html` | 5-Schritte-Wizard im Produkt; `distributed-agent-studio-target-architecture.md` | **fehlt** (Drift-Report D1 fordert ihn) |
| `architecture/bus/implementation-state.md` | Code (Stand eingefroren 05-11) | **fehlt** ("Living Doc"-Anspruch bricht) |
| `architecture/decisions/proposed/adr-0051-…` | weitgehend implementiert (Pipeline-Domain) | Status "Proposed" nicht mehr ehrlich → in adr-archive falten |
| `meta/documents/` + `meta/reports/documents/` | Sidecar-Migration (`*.meta.json`/`*.report.html` neben Quelldatei) | Audit sagt es; Ordner selbst unmarkiert |
| `reports/html/bus-architecture-report.html`, `orchestrator-system-visual-report.html` | `architecture/bus/agent-message-bus.md` bzw. Projekt-Map/Hub | **fehlt** |
| `cli/investigations/codex-runner-investigation.md` | Fix gelandet 05-12 (in sich dokumentiert) | ok (forensisch) |
| Bereits korrekt gebannert (kein Handlungsbedarf): `concepts/git-branching-integration-zielbild.md`, `concepts/project-chartroom-concept.md`, `concepts/remote-execution-product-integration.md`, `concepts/project-relationship-model.md`, `concepts/task-execution-and-log-architecture.md`, `research/remote-ready-kickoff-2026-07.md`, `meta/reports/wiki-drift-audit-2026-06-11.html`, `in-app-help/lane-guides/lane-3a-failed-pickup.md` | — | ok |

Verwaist (weder aktuell noch als historisch deklariert):
`wiki/learnings/README.md` (leer, Step nie gelaufen — Step aktivieren oder Ordner-Erklärung ergänzen);
`wiki/.drift/` (2 Einträge ohne Index); fehlende Seite `cli/skills/cli-copilot.md`
(gelöscht, aber Copilot bleibt supported — wiederherstellen oder Referenzen bereinigen).

---

## Struktur-Empfehlung

Die Grundstruktur nach der Migration 2026-06-11 ist gesund (Kategorien = Ordner, `docs/README.md` als Index). **Kein großer Umbau.** Empfohlen ist genau eine neue Ebene und wenige Moves:

Ziel-Ordnerbild (Änderungen gegenüber heute fett):

```
docs/
  architecture/  cli/  concepts/  contracts/  design/  domains/
  engineering-workstream/  frontend/  in-app-help/  meta/
  mockups/  operations/  product/  proposals/  quality/
  reports/  research/  schemas/  workbenches/
  wiki/
    common-problems/
    concepts/            (nur lebende Konzept-/Wissensseiten)
    **concepts/archive/**  (abgeschlossene Vorfalls-/Session-Analysen)
    learnings/
```

Umbenennungs-/Verschiebeplan (klein, 1 Ebene):

| Alt | Neu | Grund |
|---|---|---|
| `wiki/concepts/overnight-2026-06-23-summary.html` | `wiki/concepts/archive/…` | abgeschlossener Sessionbericht |
| `wiki/concepts/claude-termination-investigation.html` | `wiki/concepts/archive/…` | RESOLVED-Forensik |
| `wiki/concepts/runner-stability-incidents.html` | `wiki/concepts/archive/…` (nach Merge offener Invarianten in historie.html) | Chronik-Monopol liegt in der Workbench |
| `meta/documents/`, `meta/reports/documents/` | löschen oder `meta/archive/` | Vor-Sidecar-Alt-Evidenz |
| `concepts/git-branching-integration-zielbild.md` | `research/git-branching-integration-zielbild-2026-06.md` (optional) | Entwurfsgeschichte gehört zu research |
| `concepts/cli-completion-and-test-quality-gate.md` | `research/cli-completion-gate-entwurf-2026-06.md` (optional) + Banner | dito |
| `reports/html/bus-architecture-report.html`, `orchestrator-system-visual-report.html` | belassen + Historisch-Banner | Moves lohnen nicht |
| — (neu) | `cli/skills/cli-copilot.md` | Lücke schließen oder Copilot-Verweise entfernen |

Alternative mit weniger Reibung: statt `archive/`-Moves nur **Status-Banner** setzen (die
Wiki-Klassifikation kennt Direction/Health bereits) — Moves brechen Links, Banner nicht.
Empfehlung: Banner zuerst (sofort), Moves nur wenn die kuratierte Startseite steht.

Sprachkonvention-Empfehlung: **Englisch bleibt Default** (AGENTS.md-Policy; Drift-Report
moniert DE-Artefakte aktiv). Ausnahme explizit machen: Operator-Workbenches
(`workbenches/haertung…`) dürfen Deutsch sein, tragen dafür eine einzeilige EN-Summary im
Kopf. Die zwei deutschen `concepts/`-Seiten und die zwei deutschen Mockup-HTMLs sind
historisch — nicht übersetzen, nur bannern.

---

## Migrationsliste devspace → Wiki

`C:\Projects\agent-taskboard-devspace\artifacts\improvement-plans\`:

| Datei | Inhalt | Empfehlung |
|---|---|---|
| `salvage-reconciliation-2026-07-17.md` | Vollzugsprotokoll Grade-D-Welle/Salvage-Heilung, Root Cause = Spätfolge Phantom-Welle | **übernehmen**: als Quell-Referenz in `workbenches/haertung-verteilte-ausfuehrung/historie.html` einarbeiten/anhängen (die Chronik beansprucht genau diese Vorfälle) |
| `github-org-naming-handoff-2026-07-11.md` | Org-Migration agent-orc: Repos, Remotes, Publishing-Owner, offene nuget/npm-Punkte | **übernehmen** als kurze Dauer-Seite `operations/github-organization.md` (Remotes/Owner sind Betriebswissen, kein Sessionprotokoll) |
| `remote-multichat-handoff-2026-07-08.md` | Zielbild-Klarstellung Runner-Split (Mode C) + Multichat Phase 0/1 | **verlinken/archivieren**: Kernaussage ist in `distributed-agent-studio-target-architecture.md` aufgegangen; als historische Quelle aus dem Zielbild-Doc referenzieren |
| `lage-kontext-handoff-2026-07-12.md` | Wiedereinstiegs-Lagebild nach Nachtschicht 11./12.07. | **archivieren** (Sessionprotokoll); die dauerhaften Lektionen existieren bereits als common-problems (`services-killed-by-harness-sweep`, `workflow-args-json-string-fanout`) und Memory-Einträge |
| `task-detail-pipeline-ui-handoff-2026-07-07.md` | Produktionsintegrations-Brief für Task-Detail-Pipeline-UI | **verlinken** aus `mockups/task-processing-pipeline/README.md` oder übernehmen als `mockups/task-processing-pipeline/integration-handoff.md`, solange die Umsetzung offen ist |
| `task-detail-pipeline-ui-implementation-plan.md` | UI-Regeln + Umsetzungsplan (05.07., de) | wie vorstehend — zusammen mit dem Handoff behandeln (eine Seite genügt) |
| `20260521-after-f1-f11.md` | Feinschliff-Liste nach F1-F11 (Mai) | **archivieren** (überholt; Einzelpunkte sind erledigt oder als Karten aufgegangen) |

Dazu (beim Sichten gefunden, außerhalb des Auftrags-Scopes, nur Hinweis): die
`artifacts/task-detail-*`- und `task-overview-*`-HTML/PNG-Serien im Devspace-Root gehören
inhaltlich zur selben Pipeline-UI-Familie und wären Kandidaten für dieselbe Mockup-Mappe.

---

## Kurations-Seeds

Vorschlag für die kuratierte Wiki-Startseite (direkt in `home.json` übertragbar).
relPath relativ zu `docs/`.

| Sektion | relPath | Label | Begründung |
|---|---|---|---|
| Start | `README.md` | Dokumentations-Index | Vollständiger, gepflegter Einstieg mit Load-Bearing-Entry-Points |
| Zielbild | `concepts/distributed-agent-studio-target-architecture.md` | Verteiltes Zielbild (Studio/Server/Runner) | Kanonische Koordinationsquelle aller Remote-/Lifecycle-Entscheidungen |
| Härtung | `workbenches/haertung-verteilte-ausfuehrung/index.html` | Härtung der verteilten Ausführung | Aktuellstes Konzept: Result-SHA, Fencing, hermetische Verifikation |
| Härtung | `workbenches/haertung-verteilte-ausfuehrung/historie.html` | Vorfalls-Chronik | Der eine Pflegeort für Vorfälle + Präventionsstand |
| Completion/Review | `wiki/concepts/completion-review-and-remote-runner-stability.html` | Completion, Review & Remote-Stabilität | EN-Umbrella für Completion-Semantik, Provenienz, Gates |
| Betrieb | `operations/setup/getting-started.md` | Getting started | Verifizierter Null-auf-Betrieb-Pfad |
| Betrieb | `operations/remote-hosts.md` | Remote-Hosts-Runbook | Operator-Lifecycle für Runner-Hosts (mit linux-runner-host als Tiefe) |
| System | `domains/runner.md` | Runner-Domain-Map | System of record für Pickup/Run/Outcome/Recovery |
| System | `domains/pipeline.md` | Pipeline-Domain-Map | System of record für Pre/Core/Post-Steps und Kosten |
| System | `domains/tasks.md` | Tasks-Domain-Map | System of record für Lanes, Job-Ordner, API-Mutationen |
| System | `contracts/filesystem.md` | Filesystem-Contract | Kanonisches On-Disk-Layout, von jedem Agenten gebraucht |
| Entscheidungen | `architecture/decisions/adr-archive.md` | ADR-Archiv | Alle tragenden Entscheidungen an einem Ort |
| Integration | `wiki/concepts/task-integration-and-merge-workflow.md` | Task-Integration & Merge | Code-verifizierter Ist-Zustand des Merge-Wegs |
| Probleme | `wiki/common-problems/README.md` | Common Problems | Erste Anlaufstelle bei bekannten Symptomen (18 Muster) |
| UI/Design | `design/style-guide-hard-rules.md` | Design Hard Rules | Prompt-known Regelwerk für jede UI-Karte |
| Wiki-System | `concepts/wiki-pulse-dashboard.md` | Wiki Pulse | Erklärt den generierten Einstiegs-View + Grading-Anbindung |

---

## NICHT getan (bewusst)

Nichts gelöscht, nichts verschoben, keine Banner gesetzt, keine anderen Dateien geändert,
kein Commit. Diese Datei (`docs/wiki/konsolidierung-analyse-2026-07-18.md`) ist die einzige
Neuanlage. Alle Empfehlungen oben sind Vorschläge für Roberts Entscheidungspass.

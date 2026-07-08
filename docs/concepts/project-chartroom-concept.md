# Auto-curated project knowledge area — working name "Chartroom"

**Status:** concept 2026-07-08 (operator direction, Findungsphase). Related:
[`project-relationship-model`] (AGT-1984 — this area lives in the wiki's
own checkout, branch-bound), [`multichat-orchestrator.md`](multichat-orchestrator.md)
(the curator is an orchestrator context; the area is *pullable* into chats),
[`post-processing-immediacy-and-parallelism.md`](post-processing-immediacy-and-parallelism.md)
(the classification hook is a post-processing step).

## 1. The idea (operator's words, distilled)

Every project accrues a story out of its interactions and tasks. Today that
story exists only implicitly (task folders, logs, git). The Chartroom makes
it explicit and keeps it alive **automatically**:

- **Roadmap** — the big themes *ahead* ("das sind die Themen, die kommen"),
  continuously maintained, not a stale planning doc.
- **Past Map** — the opposite direction: automatically extracted big theme
  blocks from what happened; categorization + classification that sharpens
  with every interaction. A view of the *Problemwelt*.
- **Theme dossiers (Sammelbecken)** — one living page per theme that
  aggregates: "worked on this again (3×)", bugs that appeared, decisions
  taken, current state, history, timeline, outlook. Continuously
  aufbereitet, aktualisiert, geprüft.
- **Project timeline** — the chronological spine the dossiers hang off;
  can be generated retroactively from existing history.
- Everything is **pullable**: the orchestrator (any multichat context) can
  query the area ("what's the story of theme X?") instead of re-deriving it.

## 2. Data sources (all exist today)

Tasks + lanes + results/summaries, run logs + session events, decision
journals (`logs/decisions/*.jsonl`), escalations, evidence-git commits,
docs/ADRs, orchestrator chat histories (per-context, once MC-1a lands).

## 3. Mechanics — three moving parts

1. **Classification hook (pipeline step, automatic).** On task onboarding
   and on task completion, a cheap step assigns the card to one or more
   themes (taxonomy is living data, not config) and appends an event to the
   theme dossier + project timeline. This is the "der ist geonbordet"
   processing step the operator already sees — extended with theme tags.
2. **Curator (periodic, an orchestrator context of its own).** Runs on a
   schedule (or after N events): refreshes the Roadmap page from backlog/
   epics/concepts, re-clusters themes when the taxonomy drifts, verifies
   dossier claims against reality (links still valid? theme done?), writes
   history + outlook sections. This is a *pipeline experiment step* per the
   operator — start it as an automatic step on a schedule, tune later.
3. **Rendering + pull.** The area lives as pages (MD/HTML with embedded
   graphics: timeline strip, theme map) in the project's wiki space — i.e.
   inside the wiki's own checkout (AGT-1984), branch-bound like everything
   else. UI: an own tab next to Wiki in the Project Hub. API: read endpoints
   so orchestrator contexts can pull dossiers as context.

## 4. Phasing

| Phase | Scope |
|---|---|
| P1 — retroactive pilot | one-shot agent walks a project's full history → initial Past Map + timeline + first dossiers; validates the taxonomy approach on real data (Agent Studio itself is the perfect guinea pig — tonight alone would yield dossiers like "Post-processing robustness", "Remote execution", "Multichat") |
| P2 — classification hook | onboarding/completion pipeline step appends events live |
| P3 — curator | scheduled refresh of Roadmap/Past Map/dossiers, verification pass |
| P4 — pull + graphics | orchestrator query endpoint, timeline/theme-map visuals, website showcase (Product Proof) |

## 5. Naming (10 candidates)

The operator's instinct: "Wiki" doesn't hit it; "Logbook"/"Helm Book" direction.

| # | Name | Warum |
|---|---|---|
| 1 | **Chartroom** | der Raum, in dem Karten liegen und der Kurs geplottet wird — Roadmap UND Past Map sind Karten; passt zur Helm-Metapher |
| 2 | **Logbook** | nautisch, schlicht, jeder versteht es; betont aber nur die Vergangenheit |
| 3 | **Atlas** | Sammlung von Karten der Problemwelt; gut für die grafische Ambition |
| 4 | **Chronicle** | die fortgeschriebene Erzählung des Projekts |
| 5 | **Almanac** | periodisch kuratiert, Rück- und Ausblick in einem |
| 6 | **Compass** | Richtung + Herkunft; eher Roadmap-lastig |
| 7 | **Journal** | vertraut, unprätentiös; Nähe zu "decision journal" |
| 8 | **Observatory** | der Ort, von dem aus man die Entwicklung beobachtet |
| 9 | **Dossier(s)** | betont die Themen-Sammelbecken; als Bereichsname sperrig |
| 10 | **Helm** | knapp, steuerungsnah; kollidiert evtl. mit Steuerungs-UI-Begriffen |

**Empfehlung: Chartroom** (deckt Roadmap + Past Map + Kurs in einer
Metapher, einzigartig genug für Produkt-Sprache), Zweitplatzierter:
**Logbook** (wenn Vertrautheit schlägt Präzision). Kann auch im Wiki
aufgehen — dann als eigener, automatisch kuratierter Bereich "Chartroom"
innerhalb des Wikis.

## 6. Open questions (Findungsphase)

- Granularität der Themen-Taxonomie: frei clusternd vs. operator-seeded?
- Schreibrechte: Kurator schreibt nur in den Chartroom-Bereich (nie ins
  restliche Wiki) — Konvention oder technisch erzwungen?
- Kosten-Deckel: Kurator-Läufe sind LLM-Arbeit — Budget/Frequenz-Regler
  gehört ins Design (siehe Aktiv-Cap-Muster aus dem Multichat-Konzept).

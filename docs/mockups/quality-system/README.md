# Quality System &mdash; Mockup

Design exploration. **A click-dummy.** Goal: serve as the template for the next iteration of the actual app. Not committed to any naming or implementation. Things change here without ceremony.

## Files

- [taxonomy.md](taxonomy.md) &mdash; concept inventory, the two axes that separate the items, wording options with tradeoffs, recommendation. Earlier iterations of the wording debate live here.
- [ui.html](ui.html) &mdash; clickable dummy UI. Open in a browser. Catppuccin-ish dark to match the real frontend.

---

## Open conceptual questions (user-driven, captured for later editing)

The mockup currently chooses defensible defaults to keep moving, but several decisions are explicitly *not* final. Each section below records the user's competing ideas verbatim-style so the threads can be picked up separately.

### 1. What is "Quality"? Is it the right word?

> "Was ist Quality-Ding? Ist es ja eigentlich Promptmanagement."

Quality as a *top-nav* concept conflated work data with master data. This iteration drops Quality from the top nav and treats it as a function of work surfaces (Security panel on Project, Findings on Task) plus a Settings sub-area. The label "Quality" still appears as a project section header, where it's coherent ("things that grade this project's work").

Open:
- Should the project section be renamed too? Candidates: "Audits & Checks", "Skills applied", "Reviews".
- Does "Quality" stay anywhere, or fully retire?

### 2. Skills vs Prompts vs Audits/Checks &mdash; naming

The user threw out four overlapping ideas in quick succession:

1. "Wir nennen das Ding Prompts. Skill-Bibliothek + Prompt-Bibliothek." &mdash; two separate libraries.
2. "Prompts werden Subtasks von Skills." &mdash; Prompts as building blocks inside Skills.
3. "Wir sollten nur mit Skills arbeiten, und dann gibt es halt Skills." &mdash; single concept "Skill" covering everything.
4. "Importierte Prompts werden allein eingef&uuml;llt; Skills haben ein bisschen mehr." &mdash; Prompts and Skills as different richness levels.

**Picked for this iteration**: option 3. Everything reusable is a **Skill**, distinguished by `type`:
- `workflow` &mdash; agent invocation that produces work (the original Skills)
- `audit` &mdash; project-scope examination, read-only (was Audits)
- `check` &mdash; task-diff examination, read-only, runs at `progress &rarr; review` (was Task Checks)

Probes are *not* Skills (see &sect; 4).

Provisional. Worth revisiting if "Skill" feels too generic for what an Audit does, or if "Prompt" wants its own place.

### 3. Top-level work vs master data

> "Die Hauptpunkte sind Board, Projects und Skills ... Stammdaten ... auf einer anderen Ebene ... vielleicht im Drei-Punkte-Men&uuml;."

Resolved this iteration:
- Top nav = work plane: **Board &middot; Projects &middot; Skills &middot; &#9881; Settings**
- Settings = master plane: project defaults, probes, CLI config, anything configuration-shaped
- Skills top-nav surface holds *both* browse/invoke (work) and authoring/edit (master) for the dummy. In production these may split: top-nav Skills = catalog and invocation; deeper authoring under Settings.

Open: keep Skills as one combined surface, or split it?

### 4. Probes &mdash; first-class or buried?

Probes (live runtime measurements: startup latency, poll roundtrip, longtask budget) are *code*, not prompts. They don't unify with Skills.

This iteration: separate concept under Settings, surfaced on Project pages as a sub-panel inside the Quality section.

Open: promote Probes to their own top-nav entry? Or keep buried?

### 5. Skill repository discovery

> "Eine Google Suche ... vorgefertigtes Skill-Repository ziemlich geil ... zieh mal die bekanntesten coolsten Skills."

Currently a curated catalog with twelve plausible entries (mock names like ADR Writer, Threat Modeler, Test Generator). License shown on every card. No actual fetch.

Open:
- Real catalog vs. permanent mock?
- Curation source: GitHub trending? User submissions? Editor-curated only?
- Update mechanism: manual, periodic, never?

### 6. Licenses

Originally had a third tab "License notices" listing all installed skills with their licenses in a table. User found it strange and asked it removed.

Now: license tag on every skill card and on the install dialog with a one-line description; copyleft (GPL family) flagged with a &#9888; warning before install. No central license-notices page.

Open: do we still need a per-installation summary anywhere (legal review, audit export), or is per-skill display enough?

---

## What this mockup is *for*

Template for the next iteration of the app. The aim is that when the next implementation cycle begins, this folder is the source for:

- The page structure (top nav, project page sections, dashboards, dialogs)
- The conceptual model (work vs master data, Skill types, Probes as separate)
- Provisional wording (what to call what in UI labels)
- The open questions list above &mdash; so they get answered deliberately, not by accident

It is not the final design. It is a defensible draft that lets the user critique concrete artefacts instead of abstract proposals.

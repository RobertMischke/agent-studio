# Visual quality & proof — the app must always look right, and agents must prove it

**Status:** research + concept v1, 2026-07-10 evening — operator-directed
("umfassende, coole Recherche: konsistentes Layout, visuelles Feedback,
eine Skill-Welt rund um Visual Proof — werdet kreativ"). Related:
`docs/design/style-guide-hard-rules.md` (live, prompt-known — AGT-2061),
AGT-1918 (screenshot standardization, parked), withdrawn AGT-2059
(deploy smoke — operator pulled it; its ideas partially return here in a
different shape), AGT-2049 (acute vs. history), wiki-pulse (grading).

## 0. Evidence: why UI quality slips — today's five failures, dissected

| Failure (today) | Each card was "green" because… | Root gap |
|---|---|---|
| Tree regression (2057) | two nav cards passed their own specs in isolation | nobody renders the **integrated** result |
| Statusbar clutter (2058) | revamp screenshots were **mocked**, both themes | mocked proof ≠ the running app |
| Header "Kraut und Rüben" (2068) | no spec asserts alignment; lint can't see baselines | style rules exist as prose, not as **checks** |
| Icon boxes / left lines (2063) | taste rules lived only in the operator's head | rules weren't **prompt-known** until 2061 |
| e2e skipped repeatedly | worktree lacks `node_modules` (file:-dep) | agents *couldn't* look even when willing |

Conclusion: agents don't lack diligence — they lack **sight** (env), a
**contract** (machine-checkable rules), and a **duty** (proof required).

## 1. Target picture — three loops

**Loop 1 — Design contract (static, cheap, every commit).**
The style guide stops being prose: hard rules become lintable checks
(stylelint plugins / custom rules): no `border-left` accents on
cards/panels (R1), token-only colors/spacing, single-height chip rows,
`tabular-nums` on numeric columns, both-themes variables present. Runs in
the normal lint gate — a rule violation fails like a type error.
*Creative bit:* the rule file is **generated from the wiki page** (style
guide page frontmatter carries the machine rules), so operator edits the
wiki, the linter follows — wiki as the single source, prompt-known AND
build-known.

**Loop 2 — Visual proof (per card, the "skill world").**
A first-class **skill**: every UI-touching card must attach *real*
screenshots from the running, integrated app — and the skill makes that
easy instead of heroic:

- `ui-proof` skill = one command the agent calls: boots (or connects to)
  the shared verify instance, navigates to the touched surfaces (routes
  declared in the card or derived from the diff), captures both themes,
  saves labeled evidence (`--real`, never `--mocked` for acceptance).
- The **verify instance** solves the env gap: one warm stable-like stack
  (own port, own checkout, rebuilt from the task branch on demand) that
  worktree agents can target — no per-worktree `node_modules` misery.
- **Self-assessment prompt**: the skill returns the screenshots to the
  agent with the style-guide page and asks: "does this violate any hard
  rule? is anything misaligned, truncated, boxed?" — the agent fixes
  before finishing. (An LLM looking at its own screenshot catches the
  truncated "R" label instantly; today nobody looked.)
- Aspect step "**design review**" (vision): grades the evidence against
  the hard rules; verdict feeds the normal review pipeline. Mocked or
  missing evidence on a UI card = not clean.

**Loop 3 — Integrated watch (after merge, human-owned).**
The operator withdrew the automatic deploy smoke — respected. The light
variant that remains: after deploy, the **Pulse/Workstream** gets a
"fresh deploy" entry listing UI-touching cards since last deploy, each
with its evidence pair — a 60-second human flip-through instead of a bot
gate. The bot prepares the review; the human keeps the taste authority.

## 2. The skill world (wiki-anchored, per operator hunch)

Skills are **wiki pages with a contract**: prompt-known (loaded into
UI-task prompts automatically), each declaring purpose, command, evidence
expectations. Start set:

| Skill page | Gives the agent |
|---|---|
| `style-guide-hard-rules` (exists) | the taste contract, lintable |
| `ui-proof` | the capture command + self-assessment ritual |
| `layout-primitives` | the blessed flex/grid/chip/header patterns (copy-paste-able), so new UI reuses instead of reinvents — attacks "Kraut und Rüben" at the source |
| `component-map` | which component owns which surface (stops parallel cards building second truths) |

The collector/curator keep these pages honest (Workstream mechanics);
the grading run (AGT-2051) grades them like any page.

## 3. Slices (proposal for the operator's return)

| Slice | Scope | Size |
|---|---|---|
| VQ-1 | style-guide → machine rules (stylelint custom rules generated from wiki frontmatter); wire into lint gate | M |
| VQ-2 | verify instance (shared warm stack for agents) + `ui-proof` capture command + labeled evidence convention | M/L |
| VQ-3 | design-review aspect (vision grading vs. hard rules); UI cards require real evidence | M |
| VQ-4 | `layout-primitives` + `component-map` wiki pages, prompt-known; seed from existing good components | S |
| VQ-5 | post-deploy Pulse entry with evidence flip-through (human loop) | S |

Order: VQ-4 (cheap, immediate) → VQ-1 → VQ-2 → VQ-3 → VQ-5.

## 4. The review instrument (operator-directed, 2026-07-11 night) — a standard tool

Beyond per-card proof (Loop 2): a **full-app visual survey** as a reusable
instrument, run against the live stable with REAL data (the operator's
board — genuine edge cases included: overfull lanes, long titles, escalation
history chips, mixed themes).

1. **The sweep**: every surface and function screenshotted systematically
   (explorer, board states, task detail incl. pipeline/result/escalation
   panels, settings pages, wiki/pulse, search, usage) — both themes, desktop
   + narrow; edge-case states deliberately visited, not avoided.
2. **The feedback page**: one browsable page — screenshot list, and next to
   EVERY screenshot the visual findings (what is hard to read, what is good,
   what to improve; consistency, alignment, visual elements) written by a
   best-of-class model looking at the actual pixels.
3. **The proposal system (Project Hub)**: findings become **structured
   proposal documents** (wiki-form: Befund / Beleg-Screenshot / Vorschlag /
   Aufwand) listed in the Project Hub with an **approve flow** — operator
   approves → implementation card is spawned (task-spawner mechanics,
   planning-task contract). The operator curates; the machine drafts.
4. **Standard**: the sweep is repeatable (after big UI waves, before
   releases); each run's page is dated and kept — the visual history of the
   product.

Sequencing note: the first sweep runs AFTER the backlog drain and BEFORE the
website revamp (WEB-R cards) — its curated screenshots feed the website work.

---
name: regenerate-readme
description: Rebuild README.md from current product reality (ROADMAP, AGENTS, design docs) after a load-bearing change or tone refresh, following the repeatable regeneration contract.
trigger: user
mutates_code: true
mutates_queue: false
sentinel: TASKBOARD-SKILL-REGENERATE-README
---

# Skill: Regenerate README

> Sentinel: `TASKBOARD-SKILL-REGENERATE-README`. Echo this string in your first reply when the orchestrator probes whether the skill loaded.

Rebuild [`README.md`](../../../README.md) from the current product reality. README is a narrative document; it drifts whenever code, architecture, or tone changes. This skill captures the contract so the regeneration is repeatable.

## When to invoke

- After a load-bearing change to runner / orchestrator / supervisor / skill model that the README must reflect.
- After a marketing-tone refresh.
- When a periodic system review flags README drift against ROADMAP.md or AGENTS.md.

## Inputs

The README must stay in sync with these sources of truth. Read them first; never paraphrase from memory:

- [`ROADMAP.md`](../../../ROADMAP.md) - product thesis, themes, hard boundaries.
- [`AGENTS.md`](../../../AGENTS.md) - architecture pointers, orchestration philosophy, supervision model.
- [`docs/system/architecture/decisions/adr-archive.md`](../../../docs/system/architecture/decisions/adr-archive.md) - load-bearing decisions to honour.
- [`docs/quality/design-principles.md`](../../../docs/quality/design-principles.md) - UX contract.
- [`docs/system/contracts/protocol-style.md`](../../../docs/system/contracts/protocol-style.md) - Activity Log shape.
- [`backend/Endpoints/EndpointMapping.cs`](../../../backend/Endpoints/EndpointMapping.cs) - the actual route surface; never invent endpoints.

## Hard rules

These are policy, not preference. Violating any is a defect.

1. **No em dashes** anywhere in the file. Use a hyphen, a comma, a semicolon, or a new sentence. The repo policy is in [`AGENTS.md`](../../../AGENTS.md) "Documentation Language".
2. **English only** in the body. No pinned German note at the top: the previous propagation-test note was removed on 2026-05-12. Memory-propagation rules now live in AGENTS.md as structural guidance rather than a top-pin.
3. **No invented features.** Every claim in the README must be traceable to code or to one of the source-of-truth docs above.
4. **Keep the demo project images.** README references `docs/assets/images/board-overview.png`, `docs/assets/images/detail-protocol.png`, `docs/assets/images/detail-three-panes.png`, `docs/assets/images/detail-quality-gate.png`. Do not delete the demo project these came from. Regenerate README often, keep the demo intact.
5. **Sections are stable.** The top-level sections in the current README are the canonical narrative skeleton:
   - Title
   - Security first
   - The bottleneck is you (with the ASCII queue diagram)
   - Principles (table of skipped things)
   - What you see (board / detail / three-pane / review handoff, each with one image)
   - Deterministic orchestration over prompt trust (and the supervision pointer)
   - Portable skills, not CLI-local silos
   - How it's wired (ASCII boxes)
   - Running (api.sh + npm)
   - Dev vs stable checkout
6. **Cross-link to ROADMAP and AGENTS** rather than duplicating their content. README is the marketing-narrative front door; ROADMAP is intent; AGENTS is operational rules.

## Process

1. Read every source-of-truth doc listed under Inputs.
2. Diff the current README against the inputs. List drift candidates: claims that no longer hold, missing supervision section, stale endpoint shapes, broken image links, anything not in English.
3. Draft the rewrite section by section. Keep the ASCII diagrams. Keep all image references; only replace an image if a new equivalent already exists at the same path.
4. Run `grep -c "—" README.md` and confirm the result is `0`. If not, fix.
5. Run `grep -nE "^## " README.md` and confirm the section list matches the canonical skeleton. Add or restore missing sections; never silently delete one.
6. Verify every internal link resolves: `grep -oE "\[.*\]\(\S+\)" README.md | grep -v "^http"` and spot-check.
7. Compare the rewrite to the previous version and write a one-paragraph commit message describing what drift was closed.

## Output

One commit, message shape: `docs(readme): regenerate to track <thing that drifted>`.

Do not silently rewrite ROADMAP.md or AGENTS.md from this skill. If the rewrite reveals drift in those, surface it as separate follow-up tasks.

## Anti-patterns

- Rewriting tone-by-vibe without re-reading the source docs. The README will drift further from reality.
- Adding em dashes "for flow". The lint sweeps them out; do not waste a round-trip.
- Adding a new top-level section without first updating the canonical skeleton list above.
- Adding a German top-pinned propagation note back. That experiment was rolled back on 2026-05-12; the propagation rule lives in AGENTS.md now, not as a README header.
- Inventing endpoints, file paths, or component names. README is read by people deciding whether to use the product; false claims erode trust faster than missing claims.

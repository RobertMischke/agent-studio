# Quality System - Mockup

Design exploration. **A click-dummy.** Goal: give the next implementation cycle a concrete reference without pretending the whole thing is ready to build.

The current direction is deliberately narrower than the first visual draft:

- Security is the first product surface, not generic "Quality".
- Audits, Task Checks, Performance Probes, and Skills stay distinct.
- Findings are evidence for human review. They do not block task state transitions in the first cut.
- Skills remain reusable workflows that produce work. Audits and Task Checks may use skill-like prompt bodies, but they are review definitions, not the same product concept.
- Repository-style skill discovery is a later layer. Local installed skills, licenses, and project lookup must work first.

## Files

- [taxonomy.md](taxonomy.md) - concept inventory, vocabulary, storage shape, and implementation order.
- [ui.html](ui.html) - clickable dummy UI. Open in a browser. Catppuccin-ish dark to match the real frontend.

## Current Recommendation

Use concrete surfaces instead of a vague top-level Quality product:

- **Security** on the project page, visually promoted.
- **Audits and Checks** on the project page for configured review definitions.
- **Task Checks** on the task detail/review surface.
- **Performance Probes** under diagnostics/settings and surfaced on projects once implemented.
- **Skills** as the local reusable workflow catalog.

The mockup still has an internal `#/quality` route for the definitions library because it is a dummy. Product UI should call that surface "Review definitions" or "Audits and Checks" until the word "Quality" proves useful in real use.

## Critical Boundaries

This design must not turn the app into a workflow engine.

- A Task Check can create findings, chips, and follow-up task suggestions.
- A Task Check must not silently hold a task in `3-progress` in the first version.
- A spawned check is a separate CLI invocation and must still respect one active coding task per project.
- Check results are review evidence, not automatic permission to modify code.
- Follow-up work becomes a normal queued task.

## First Implementation Slice

1. Project Security panel with baseline state and review history.
2. Review definition model for Audits and Task Checks, stored as Markdown with frontmatter.
3. Per-project Task Check defaults.
4. One spawned Task Check after a main task finishes, writing structured findings into the job folder.
5. Findings visible on the task review surface with a "create follow-up task" action.
6. Local Skills catalog for installed workflow skills.
7. Performance Probe slots after the audit/check loop is stable.

## Open Questions

### 1. Does the word "Quality" survive?

Probably not as top navigation. It is too broad and too easy to confuse with prompt/skill management. Keep the word only where it describes a category, not a destination.

Candidate labels:

- Audits and Checks
- Review definitions
- Project checks
- Evidence rules

### 2. Are Audits and Task Checks Skills?

No, not as product concepts.

They may reuse the same storage mechanics as skills, and they may render prompt bodies for agent runs, but they answer a different question:

- Skills produce or transform work.
- Audits and Task Checks examine work.
- Probes exercise a running system and measure it.

That separation keeps the skill system portable and keeps review policy deterministic.

### 3. When do checks run?

First cut: after the main task run completes and before or during review handoff, but without blocking state movement. The output is visible evidence for the reviewer.

Later versions can add stricter policy, but only after the non-blocking evidence loop is boring and trustworthy.

### 4. What about a skill repository?

Keep it as a future preview in the mockup. Do not ship internet discovery, install, update, or third-party execution until local skills have:

- A canonical installed list.
- License metadata.
- Explicit source and version records.
- Project README lookup.
- A controlled installer path.

## What This Mockup Is For

When the next implementation cycle starts, this folder should answer:

- Which concepts are separate.
- Which surface gets built first.
- What not to build yet.
- Which terminology is safe enough for the UI.
- Where the roadmap intentionally narrows the design.

It is not the final design. It is a stricter draft that turns a broad visual exploration into an implementable sequence.

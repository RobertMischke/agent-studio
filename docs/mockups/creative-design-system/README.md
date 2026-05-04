# Creative Design System - Mockup

Design exploration. Goal: describe how Agent Task Processor can help produce beautiful software through design loops, screenshots, and structured critique.

This is a sibling to the quality-system mockup, not a replacement for it:

- Quality asks whether the work is correct, safe, and reviewable.
- Creativity and Design asks whether the work is coherent, expressive, usable, and worth shipping as product.

## Files

- [taxonomy.md](taxonomy.md) - concepts, loop types, council roles, storage shape, and implementation order.
- [ui.html](ui.html) - clickable dummy for a design loop, screenshot variants, and council critique.

## Current Direction

Beautiful software needs a loop, not a single pass.

The app should support a design iteration cycle:

1. Define design intent.
2. Implement or mock a version.
3. Capture screenshots.
4. Run council-style critique.
5. Let the orchestrator decide: accept, request another version, or create follow-up tasks.
6. Preserve the chosen direction and rejected alternatives as evidence.

This matters because a coding agent can produce a working UI that is still visually weak. The product should make it normal to ask for the next version, compare screenshots, and apply critical feedback before the task is accepted.

## Council Concept

A Council is a structured critique pass with multiple roles. It is not parallel implementation.

Recommended first roles:

- Product: does the screen support the user's real workflow?
- Visual Design: does it look intentional, balanced, and polished?
- Interaction Design: are controls, states, and flows ergonomic?
- Frontend Engineering: is the design implementable without fragile tricks?
- Accessibility: can users with different needs operate it?
- Marketing and Positioning: does the screen carry the product story?

The orchestrator reads the council notes and chooses the next step. The council gives opinions; the orchestrator owns the decision.

## Critical Boundaries

- Design loops may create screenshots, critique, design briefs, and follow-up tasks.
- Design loops must not start parallel coding tasks inside one project.
- Council output is evidence, not an automatic mandate.
- The first implementation should not build a full design tool.
- Generated or searched visual references are allowed only as task evidence or design inspiration, not as hidden product dependencies.

## First Implementation Slice

1. Design evidence format for screenshot variants and council notes.
2. Screenshot comparison panel in task detail.
3. Local design Skills for screenshot critique, UI polish, copy tone, and accessibility design review.
4. Council review prompt with role-separated critique.
5. "Next version" action that creates a follow-up task from council feedback.
6. Project-level design memory for accepted visual direction and examples.

## What This Mockup Is For

This mockup should help future implementation tasks answer:

- What does a design loop produce?
- What does the council critique?
- Which artifacts stay with the task?
- How does the orchestrator choose "next version"?
- How does the product stay sequential while still getting richer critique?

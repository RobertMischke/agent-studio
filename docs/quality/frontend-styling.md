---
styleGuideId: frontend-styling
title: Frontend styling context
version: 1
summary: Compact button, label, type, panel, spacing, and color rules for styling cards.
promptSummary: Apply docs/quality/frontend/style-guide/living-rules.md. Use one primary action per group; secondary for safe alternatives, danger only for destructive actions, ghost for tertiary actions. Labels never imitate button chrome. Use sentence case; uppercase only via .studio-label, .studio-metric__label, or m.type-label. Use spacing and semantic color tokens, Deck-Panel mixins, both themes, and no colored left status edge. Extend the guide/Open cases instead of inventing a local exception.
appliesTo: {"projects":["*"],"technologies":["angular"],"taskAreas":["frontend"]}
---

# Frontend styling context

Attach this block to every styling card and styling prompt.

1. Read `docs/quality/frontend/style-guide/living-rules.md`.
2. Use one primary action per group. Use secondary for safe alternatives,
   destructive only for destructive impact, and ghost for tertiary actions.
3. Keep every sibling text button on one geometry and typography contract.
4. Labels are static: no button height, action border, raised surface, hover,
   pointer cursor, focus ring, or pressed state. Prefer a semantic dot.
5. Use sentence case. Uppercase is allowed only through `.studio-label`,
   `.studio-metric__label`, or `m.type-label`; preserve real acronyms.
6. Use `--studio-spacing-*` and semantic aliases. Never add raw spacing.
7. Use `m.deck-panel`, `m.deck-panel-muted`, and `m.deck-panel-attention`;
   never nest a second panel shell around an existing panel.
8. Use semantic `--studio-*` / `--severity-*` colors only. Never add raw colors.
9. Do not add a colored left status edge. Verify dark and light themes.
10. Extend the living guide or its Open cases when a reusable case is missing.

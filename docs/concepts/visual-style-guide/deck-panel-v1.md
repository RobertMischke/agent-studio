# Deck-Panel v1

Status: recommended for Deck convergence, 2026-07-23.  
Decision owner: Visual Style Guide Workbench, AGT-2337.  
Delivery consumer: Deck page unification, AGT-2280.

The [Deck Audit](deck-audit.html) compares the current Project Deck sections,
shows paired theme evidence, and provides an interactive panel-style
configurator. This page is the implementation-sized recommendation extracted
from that review.

## Decision

Use one quiet, tokenized panel shape for bounded content in the Deck:

- `background: var(--studio-card-bg)`;
- `border: 1px solid var(--studio-card-border)`;
- `border-radius: 10px`;
- `padding: var(--studio-spacing-4)`; and
- no resting shadow.

The section heading and description live outside the panel. A panel contains
one bounded unit of content, not an entire full-bleed workbench. This removes
the current gray-box drift without replacing it with layers of nested cards.

## Variants

| Variant | Use | Surface |
|---|---|---|
| `default` | Forms, summaries, bounded inspectors, definition cards | Card background and standard card border |
| `muted` | Read-only guidance, inherited values, settled history | `--studio-card-muted-bg` and `--studio-card-muted-border` |
| `attention` | A state that is acute now | Whole-surface semantic tint and strengthened semantic border |
| `full-bleed` | Wiki, Git View, Prompts, and comparable split-pane tools | No outer panel; bounded children may use the other variants |

`attention` never means "important content" or historical failure. It is only
for a current state requiring operator attention. It never uses a colored left
stripe.

## Header and spacing contract

1. The Deck section owns the page title and one short description.
2. Sibling panels use `gap: var(--studio-spacing-3)`.
3. A panel uses `var(--studio-spacing-4)` inset.
4. A panel header uses a title, optional eyebrow, optional quiet status badge,
   and no decorative icon unless the icon communicates the content type.
5. Panel body copy reads `--studio-fg` or `--studio-fg-dim`.
6. A settled state uses the muted variant. An acute state may use attention.

Do not wrap an existing card in Deck-Panel v1. Migrate the existing card shell
onto the contract or remove the redundant outer container.

## CSS reference

```scss
.deck-panel {
  padding: var(--studio-spacing-4);
  border: 1px solid var(--studio-card-border);
  border-radius: 10px;
  background: var(--studio-card-bg);
}

.deck-panel--muted {
  border-color: var(--studio-card-muted-border);
  background: var(--studio-card-muted-bg);
}

.deck-panel--attention {
  border-color:
    color-mix(in srgb, var(--studio-error) 55%, var(--studio-card-border));
  background:
    color-mix(in srgb, var(--studio-error) 8%, var(--studio-card-bg));
}
```

The snippet defines the visual contract, not a second token registry. Production
code continues to read the semantic tokens owned by
`frontend/src/styles/_tokens-semantic.scss`.

## Component recommendation for AGT-2280

Extract one shared Deck panel or section primitive only if it can replace the
existing outer shells without adding nesting. Its public API should stay small:

```text
variant = default | muted | attention
title
description?
eyebrow?
status?
content slot
```

`full-bleed` should be a host layout decision, not a panel variant that renders
an empty wrapper. Wiki, Git View, Prompts, and other tool workbenches should
continue to fill their pane.

## Acceptance boundary for Deck unification

- Every migrated bounded panel resolves to the same surface, border, radius,
  inset, and header rhythm.
- Both themes use semantic tokens and have before/after screenshot evidence.
- No component introduces a hardcoded gray, a decorative left status bar, or
  a resting shadow.
- Muted and attention variants are selected by meaning, not visual preference.
- Full-bleed tools do not gain an artificial outer card.
- Any justified deviation is added to the Deck Audit as a named variant.

## Evidence

The Deck Audit includes one real screenshot for each section in the 2026-07-23
review set and switches paired evidence with the page theme. AGT-2337 stores
light and dark captures of the complete audit, the configured v1 specimen, and
each audit card in its task `results/` folder.

## Living knowledge log

- **2026-07-23:** Recommended Deck-Panel v1 as the convergence baseline for
  AGT-2280 after auditing the 15 Deck sections and exposing the current style
  dimensions through a theme-aware configurator.

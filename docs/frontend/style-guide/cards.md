# Cards

A bounded surface — title row + body + optional footer or hover-lift. Today there is **no `<app-card>` Angular component** because card surfaces tend to carry feature-specific slots (drag handles, run state inputs, severity stripes). The convergence target is the **shape**: every card reads the same tokens for background, border, padding, and elevation.

See [audit-cards.md](./audit-cards.md) for the inventory.

## Shape contract

A card SCSS class should read all of:

| Slot           | Token / value                                                |
| -------------- | ------------------------------------------------------------ |
| Background     | `var(--studio-bg-elevated)`                                   |
| Border         | `1px solid var(--studio-border)` (or `--studio-border-strong` for stronger separation) |
| Border radius  | `8px` (shape step `lg`) — kanban cards, panel section cards  |
| Padding        | `var(--studio-spacing-3)` (12px) default, `var(--studio-spacing-4)` (16px) for spacious |
| Gap (inside)   | `var(--studio-spacing-2)` (8px) between rows                  |
| Elevation      | `var(--elevation-card)` if the card is "lifted" off the surface; otherwise none |

```scss
.my-feature-card {
  background: var(--studio-bg-elevated);
  border: 1px solid var(--studio-border);
  border-radius: 8px;
  padding: var(--studio-spacing-3);
  display: flex;
  flex-direction: column;
  gap: var(--studio-spacing-2);
}

.my-feature-card--elevated {
  box-shadow: var(--elevation-card);
}

.my-feature-card--accent {
  border-left: 3px solid var(--studio-accent);
  padding-left: calc(var(--studio-spacing-3) - 3px);
}
```

## Variants

Today every card is "default" + an optional accent stripe + an optional shadow. Three conceptual variants emerge from the audit:

| Variant     | Background                | Border                          | Elevation        | Use it for                            |
| ----------- | ------------------------- | ------------------------------- | ---------------- | ------------------------------------- |
| `default`   | `--studio-bg-elevated`    | `1px solid --studio-border`      | none             | Hub section card, sidebar card        |
| `elevated`  | `--studio-bg-elevated`    | `1px solid --studio-border-strong` | `--elevation-card` | Kanban card, run card                |
| `accent`    | `--studio-bg-elevated`    | left-stripe in accent or severity | none           | Task status card, drift card, steer card |

## Open question — should we ship `<app-card>`?

Extracting `<app-card variant="default|elevated|accent">` would absorb 12+ card classes onto a single component. The cost: the existing cards carry feature-specific slots that need to project through `<ng-content>`.

**Decision deferred** — the realistic next step is the **shape-and-token convergence** (already documented above): every card reads the same tokens. That captures most of the visual consistency without committing to a shared Angular component.

Proposal in [migration-status.md](./migration-status.md) "F-Cards: Decide canonical".

## DON'Ts

- **Do not** use `--studio-bg-editor` for a card. Cards are *raised* surfaces; the editor is the body canvas.
- **Do not** mix `border` and `box-shadow` to fake a thicker border. Use `--studio-border-strong` if the existing border is too soft.
- **Do not** introduce a new corner radius. Pick from the shape scale (`4 / 6 / 8 / 10 / 12 px`).
- **Do not** raise the padding above `--studio-spacing-4` (16px) for a card body. If the contents need more breathing room, it is probably a panel, not a card.

## Light + dark

Every card variant in the contract flips automatically because all four tokens have light + dark variants. The accent stripe also flips through `--studio-accent` / `--severity-*`.

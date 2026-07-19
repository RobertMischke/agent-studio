# Modals

Canonical: [`<app-dialog>`](../../../../frontend/src/app/components/dialog/dialog.component.ts). Centred panel, backdrop click cancels, `alertdialog` ARIA, Esc cooperation with `ModalStackService`. **Use this for every blocking decision** — confirms, errors, forms, create flows, settings.

For the **non-blocking right-edge panel** (orchestrator chat, CLI usage, kanban filter), use [`<app-sidesheet>`](../../../../frontend/src/app/components/sidesheet/sidesheet.component.ts). The two shapes are deliberately separate components; they look similar but their interaction semantics differ.

## Basic usage

```html
<app-dialog
  eyebrow="Confirm delete"
  title="Delete this task?"
  role="alertdialog"
  kind="danger"
  size="sm"
  testid="confirm-delete-dialog"
  (closeRequest)="onCancel()"
  (backdropClick)="onCancel()">

  <p>This removes the job folder and all its files.</p>

  <div footer>
    <button type="button" class="btn btn--ghost" (click)="onCancel()">Cancel</button>
    <button type="button" class="btn btn--danger" (click)="onDelete()">Delete</button>
  </div>
</app-dialog>
```

## Inputs

| Input        | Default     | Description                                                        |
| ------------ | ----------- | ------------------------------------------------------------------ |
| `eyebrow`    | `null`      | Small uppercase label above the title                              |
| `title`      | `''`        | Visible heading + `aria-label`                                     |
| `subtitle`   | `null`      | Optional one-line caption under the title                          |
| `role`       | `'dialog'`  | `'alertdialog'` for confirms / errors; `'dialog'` for forms        |
| `width`      | `null`      | Px width override; default 520                                     |
| `closable`   | `true`      | Show the close button in the header                                |
| `kind`       | `'default'` | `'danger'` (red top stripe) or `'primary'` (orange top stripe)     |
| `size`       | `'md'`      | `'sm'` shrinks body padding to 16px for confirm-style dialogs      |
| `testid`     | `null`      | Drives `[data-testid="<value>"]` + `-overlay` + `-close`           |

## Outputs

- `closeRequest`: emitted by Esc or the close button.
- `backdropClick`: emitted when the backdrop (overlay area) is clicked. Opt-in so callers can keep full control over cancellation semantics.

## Padding contract — `--studio-modal-padding*`

The dialog reads one base body-padding token plus slot-specific aliases:

| Token                              | Default                          | Slot           |
| ---------------------------------- | -------------------------------- | -------------- |
| `--studio-modal-padding`           | `var(--studio-spacing-5)` 24px   | Base body-padding knob |
| `--studio-modal-padding-body`      | `var(--studio-modal-padding)`    | `<app-dialog size="md">` body |
| `--studio-modal-padding-body-sm`   | `var(--studio-spacing-4)` 16px   | `<app-dialog size="sm">` body |
| `--studio-modal-padding-header`    | `var(--studio-spacing-4)` 16px   | header (every dialog) |
| `--studio-modal-padding-footer`    | `var(--studio-spacing-4)` 16px   | footer (every dialog) |

**To widen every default dialog body**, change `--studio-modal-padding` in `_tokens-semantic.scss`. The new value applies to every consumer in light + dark. This is the **single knob** the operator can dial.

**To narrow a specific dialog**, set `size="sm"`. Do not override the token from a per-feature SCSS.

## Sizes

| `size` | Body padding | Use it for                                                 |
| ------ | ------------ | ---------------------------------------------------------- |
| `md`   | 24px         | Form, settings, create flows, multi-line errors            |
| `sm`   | 16px         | Confirm dialog with a one-line message + two buttons       |

`size` controls the **body padding** only; header and footer always use the `--header` / `--footer` tokens regardless of size.

A future `size="lg"` is intentionally not shipped today. Per [`docs/quality/frontend/design-system.md`](../design-system.md) and the operator preference for information-dense surfaces, a "spread-out" variant has no current use case. Add it only when a real dialog wants it.

## Kind (accent stripe)

| `kind`      | Accent stripe (top 3px)             | Use it for                |
| ----------- | ----------------------------------- | ------------------------- |
| `default`   | none                                | Forms, settings           |
| `primary`   | `--studio-accent` (orange)          | Soft confirm              |
| `danger`    | `--severity-high` (red)             | Destructive confirm, error|

The stripe is a 3px `border-top` on the dialog panel; it stays inside the rounded corners.

## Backdrop and modal stack

- **Backdrop**: `--studio-scrim` (semi-opaque). The overlay sits at `z-index: 1000`. Confirm dialogs sit on top of normal dialogs through `ModalStackService`.
- **Esc**: handled by the host listener; emits `closeRequest`.
- **Backdrop click**: opt-in via `(backdropClick)`. Some dialogs (verbose-debug, log overlays) intentionally do **not** close on a mis-click.

## Don'ts

- **Do not** build a per-feature `.my-feature__modal` SCSS class. If your case needs a wider panel, use `[width]`; if it needs different padding, use `size="sm"`.
- **Do not** hardcode the `z-index`. The component owns it.
- **Do not** wrap the dialog in a custom overlay. The component owns the overlay element.
- **Do not** ship a dialog without a focus-trap. `ModalStackService` returns focus to the previously-focused element on close; if you bypass `<app-dialog>` you lose that.
- **Do not** ship a dialog without a `testid`. Every dialog goes through Playwright eventually.

## Light + dark

Every dialog flips automatically because `--studio-bg-editor`, `--studio-bg-sidebar`, `--studio-scrim`, `--elevation-modal`, and `--studio-fg*` all have light + dark variants. The `kind="danger"` / `kind="primary"` stripes also flip via `--severity-high` / `--studio-accent`.

## What about overlays that aren't `<app-dialog>`?

A handful of per-feature overlays exist today (verbose-debug, orchestrator-settings, cli-usage-detail, update-center, media-lightbox). They predate this contract. The migration target: have each of those read `--studio-modal-padding-*` and `--studio-scrim` even if the surface stays a separate component. Tracked in [migration-status.md](./migration-status.md) "M-Modal: Bring per-feature overlays onto modal-padding tokens".

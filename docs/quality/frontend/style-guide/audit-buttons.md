# Audit — Buttons

Inventory of every small-button-like surface in `frontend/src/app/`. Used to **find a similar implementation before authoring a new one** and to drive the migration plan in [migration-status.md](./migration-status.md).

The table groups by family. Within a family the goal is convergence; deltas worth keeping become variants on the canonical, not new classes.

## Family A — Icon-only square / near-square button

Canonical: SCSS mixin `m.icon-button($size)` in [`frontend/src/styles/_mixins.scss`](../../../../frontend/src/styles/_mixins.scss). Default size 22 px. Borderless, transparent background, hover wash from `--studio-bg-hover`, `border-radius: 3px`, `outline: 2px solid var(--studio-accent)` on focus-visible.

| Site                                                                                                       | Size  | Notes                                       | Reads canonical? |
| ---------------------------------------------------------------------------------------------------------- | ----- | ------------------------------------------- | ---------------- |
| `studio-shell.component.scss` (activity-bar trigger row)                                                   | 22px  | `@include m.icon-button(22px)`              | ✅ yes           |
| `sidesheet.component.scss` (sheet close)                                                                   | 26px  | `@include m.icon-button(26px)`              | ✅ yes           |
| `markdown-rich-editor.scss` (toolbar)                                                                      | 22px  | `@include m.icon-button(22px)`              | ✅ yes           |
| `pane-header.component.scss` (pane maximize/hide)                                                          | 24px  | `@include m.icon-button(24px)`              | ✅ yes           |
| `dialog.component.scss` (modal close)                                                                      | 26px  | `@include m.icon-button(26px)`              | ✅ yes           |
| `.icon-btn` in `mockups/next-gen-chat/` (designer kit)                                                     | n/a   | Designer mockup, not shipped                | n/a              |
| `.evidence-btn` in `review-evidence-panel.component.scss`                                                  | ?     | One-off; not on mixin                       | ❌ migrate       |
| `.ov-copy-btn` in `overview-pane.component.scss`                                                            | ?     | One-off copy button                          | ❌ migrate       |
| `.cli-usage-modal__icon-button` in `cli-usage-detail-modal.scss`                                            | ?     | One-off; per-feature                         | ❌ migrate       |
| `.chat__icon-btn`, `.md-editor__icon-btn`, `.upd-center__icon-btn`                                          | ?     | Three further variants in chat / editor / update-center | ❌ migrate |
| `.devtools-menu__trigger` in `app.scss`                                                                     | 28px  | Inline in app.scss, uses `--header-btn-height` | ❌ migrate    |

**Findings.** Five call sites already read the mixin — the canonical works. Five+ legacy call sites duplicate the recipe. The mixin signature only takes `$size`; if migration needs a darker hover or a square-vs-rounded variant, the mixin should grow a `$variant` parameter (proposal in [buttons.md](./buttons.md)). Avoid forking the mixin into N variants without first checking whether a single CSS-custom-property override would do.

## Family B — Header / chrome button (small text + optional icon)

Canonical: `.header-btn` in [`frontend/src/app/app.scss`](../../../../frontend/src/app/app.scss). Reads four `--header-btn-*` CSS custom properties (height 28px, radius 6px, padding-x 10px, gap 6px) so every chrome control aligns to one baseline.

| Site                                                                                              | Geometry    | Reads canonical? |
| ------------------------------------------------------------------------------------------------- | ----------- | ---------------- |
| Top header chrome (project chips, action buttons, dropdown triggers, kebab) — uses `.header-btn` directly or via per-component rules copying the vars | 28×auto, radius 6px | ✅ shared vars |
| `.header-btn--icon` (square header icon button)                                                   | 28×28       | ✅ same vars     |
| `.devtools-menu__trigger` (header.scss inline) — same geometry, copy-paste                        | 28×28       | ❌ duplicates the rule literally |
| `auto-review-indicator`, `update-version-badge`, `filters-dropdown`, `kanban-filter-sidesheet` trigger | varies | ⚠️ each component re-declares geometry; should read the `--header-*` vars |

**Findings.** The `--header-btn-*` CSS-custom-property contract works — it is the closest the codebase has to a "shared button geometry" today. The four-var scheme is worth lifting out of `app.scss` into `_tokens-semantic.scss` (the place every component already reads tokens from) and renaming the family to `--studio-chrome-btn-*`. Concrete proposal in [buttons.md](./buttons.md).

## Family C — Footer action button (`.btn`, `.btn--ghost`, `.btn--primary`, `.btn--danger`, `.btn--create`)

Canonical: `.btn` in [`frontend/src/app/app.scss`](../../../../frontend/src/app/app.scss). Inherits the header-btn height + radius vars, fills `padding: 0 14px`, ships variants for ghost / primary / danger / create.

| Site                                                                                              | Variant         | Reads canonical? |
| ------------------------------------------------------------------------------------------------- | --------------- | ---------------- |
| `<app-dialog>` footer (confirm, error, e2e-cleanup, workspace-create, create-task) — `.btn`, `.btn--primary`, `.btn--danger`, `.btn--ghost` | ✅ canonical    | ✅               |
| `task-detail.scss`                                                                                | mixed           | ⚠️ partial       |
| `protocol-pane.component.scss`                                                                    | mixed           | ⚠️ partial       |
| `command-deck.component.scss`                                                                     | mixed           | ⚠️ partial       |
| `update-stable-console.component.scss`                                                            | mixed           | ⚠️ partial       |
| `e2e-cleanup-dialog.component.scss`                                                               | mixed           | ⚠️ partial       |

**Findings.** `.btn` is **the** action-button shape. Variants are scoped (default / ghost / primary / danger / create). Three issues: (a) the class lives in `app.scss` not a token file or a component; reaching it is implicit ("every template is under the app shell so the class is available"); (b) `.btn--create` is a one-off purple variant; review whether it should live on the canonical or be re-built on top of an accent recipe; (c) the canonical itself reads raw `rgba(...)` values for background / border, which violates the "no hex / rgb in component SCSS" rule (baseline debt, tracked in stylelint override).

The opportunistic migration target is to lift `.btn` into a real `<app-button>` Angular component with `size="sm|md|lg"` + `variant="primary|secondary|ghost|danger"` and have the existing `.btn` SCSS become the implementation. **Decision deferred** — see [buttons.md](./buttons.md) "Open question: do we need `<app-button>`?".

## Family D — Studio-shell internal buttons (`.studio-button`, `.studio-button--primary`, `.studio-button--ghost`)

Lives only in [`studio-shell.component.scss`](../../../../frontend/src/app/features/studio-shell/studio-shell.component.scss). Used by the studio-shell internal panels. Shape differs from `.btn` (radius 4px vs 6px, font-size 12px vs 12px+weight 600, padding 6×12 vs 0×14). Currently 1 file.

**Findings.** Either (a) deprecate `.studio-button` and migrate all consumers to `.btn` + `--variant`, or (b) keep it as a studio-shell-internal style. Pick (a) once `<app-button>` lands; the differences are small and unintentional.

## Family E — Feature-scoped per-component buttons (`.chat__send-btn`, `.create-dialog__attach-btn`, ...)

Inventory (non-exhaustive): `.chat__icon-btn`, `.chat__send-btn`, `.chat__toolbar-btn`, `.commandbar__cli-btn`, `.conv__head-btn`, `.create-dialog__attach-btn`, `.create-dialog__enhance-btn`, `.create-dialog__generate-btn`, `.detail__pager-btn`, `.detail__title-edit-btn`, `.git-view__commit-chain-button`, `.lightbox__path-btn`, `.md-editor__icon-btn`, `.msg__more-btn`, `.obs__fixture-btn`, `.pane__interim-btn`, `.pchat__chip-btn`, `.pchat__step-btn`, `.prt__fixture-btn`, `.psd__child-btn`, `.psd__src-btn`, `.psec__file-btn`, `.psec__new-btn`, `.psr-modal__fix-btn`, `.psr__check-btn`, `.sheet__draft-btn`, `.statusbar__item--btn`, `.steer-card__upload-btn`, `.tup__expensive-btn`, `.tup__heatmap-row-btn`, `.upd-center__icon-btn`, `.vdbg__link-btn`, `.wss__win-btn`, `.wtt__win-btn`.

**Findings.** ~35 feature-scoped button classes. Many are wrappers around the icon-button mixin with one tweak; a few are genuine one-offs (e.g. `.git-view__commit-chain-button` renders a chained git visual that has no analogue elsewhere). The migration plan does not try to consolidate them in one go — that would be a big-bang. Instead, the migration approach is: **when you touch a file in column 1, migrate its button to the canonical and remove the per-feature class.** Tracked per-file in [migration-status.md](./migration-status.md).

## Summary

Three real families: **icon-only** (mixin works), **chrome button** (`.header-btn` shared vars work), **action button** (`.btn` works but lives in app.scss, missing Angular wrapper). The bulk of the cleanup is **not "ship a new canonical"** but **"converge ~40 feature-scoped variants onto the existing three"**. The open question is whether the icon-button + chip mixin pattern is good enough or whether the canonical should become `<app-icon-button>` / `<app-button>` Angular components for type-safe variants — see [buttons.md](./buttons.md).

# Audit — Modals, Popovers, Tooltip Surfaces

Inventory of overlay surfaces. The shell separates **modal** (centred, blocking, backdrop click cancels) from **side sheet** (push, not overlay; see [`frontend/AGENTS.md`](../../frontend/AGENTS.md) "Side-sheet layout contract") from **popover / dropdown** (attached to a trigger) from **tooltip** (instant hover, body-mounted singleton).

## Modal — canonical: `<app-dialog>`

Lives in [`frontend/src/app/components/dialog/dialog.component.ts`](../../frontend/src/app/components/dialog/dialog.component.ts). Inputs: `eyebrow`, `title`, `subtitle`, `role`, `width`, `closable`, `kind=default|danger|primary`, `size=sm|md` (new), `testid`. Reads `--studio-modal-padding-*` for body / header / footer paddings; `--studio-scrim` for the backdrop; `--elevation-modal` for the lift.

| Consumer                                              | Where                                                                                                                          | Size  |
| ----------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------ | ----- |
| Confirm dialog                                        | `components/app-dialog/confirm-dialog/confirm-dialog.component.html`                                                            | `sm`  |
| Error dialog                                          | `components/error-dialog/error-dialog.component.html`                                                                           | `md`  |
| Create-task dialog                                    | `features/board/components/create-task-dialog/`                                                                                 | `md`  |
| E2E-cleanup dialog                                    | `features/dev-tools/components/e2e-cleanup-dialog/`                                                                             | `md`  |
| Workspace-create dialog                               | `features/shell/components/workspace-create-dialog/`                                                                            | `md`  |
| Verbose-debug overlay                                 | `features/verbose-debug/components/verbose-debug-overlay/`                                                                      | `md`  |
| Orchestrator-settings modal                           | `features/orchestrator/components/orchestrator-settings-modal/`                                                                 | `md`  |
| CLI-usage-detail modal                                | `features/tokens/components/cli-usage-detail-modal/`                                                                            | `md`  |
| Update-center modal                                   | `features/update/components/update-center/`                                                                                     | `md`  |
| Media lightbox                                        | `components/media-lightbox/`                                                                                                    | n/a (own surface) |

**Findings.** Six of the ten consumers use `<app-dialog>` directly. Four (verbose-debug, orchestrator-settings, cli-usage-detail, update-center, media-lightbox) have a per-feature overlay class (`.vdbg__overlay`, `.update-center__overlay`, `.lightbox__overlay`, ...) that re-implements the modal shape. Per-feature reasons exist (the media lightbox needs a full-viewport surface; the update-center wants a wider panel) but the body padding question is the same one.

**Modal-padding fix in this run** updates `<app-dialog>` body / header / footer to read `--studio-modal-padding-*`. Per-feature overlays do not benefit automatically — they need to opt in. Tracked in [migration-status.md](migration-status.md).

## Popover / Dropdown — canonical: `<app-menu>`

Lives in [`frontend/src/app/components/menu/menu.component.ts`](../../frontend/src/app/components/menu/menu.component.ts). Text-only rows (no leading icons; see AGENTS.md "Menu surfaces are text-only"). The component owns shape + focus + keyboard navigation.

| Consumer                                              | Where                                          |
| ----------------------------------------------------- | ---------------------------------------------- |
| Tab right-click menu                                  | `features/studio-shell/`                       |
| Card right-click menu                                 | `features/board/`                              |
| Detail-header title menu                              | `features/job-detail/`                         |
| Status-bar CLI / model pickers                        | `features/shell/components/status-bar/`        |
| Project picker                                        | `features/studio-shell/`                       |
| Markdown-editor mode toggle                           | `components/markdown-rich-editor/`             |
| Protocol-pane overflow                                | `features/job-detail/components/protocol-pane/`|
| Chat model badge                                      | `features/job-detail/components/chat-model-badge/` |

**Findings.** Canonical works. No per-feature popover SCSS competing today.

## Tooltip — canonical: `[appTooltip]` directive on `<app-tooltip>`

Singleton tooltip mounted on the body. Instant on hover, HTML body supported. The directive is the only way to render a tooltip; tooltip SCSS lives in one place.

**Findings.** Canonical works.

## Side sheet — canonical: `<app-sidesheet>` + per-callsite host

Documented in [`frontend/AGENTS.md`](../../frontend/AGENTS.md) "Side-sheet layout contract": three coordinated pieces (flex parent in `app.html`, `:host` width-animated wrapper, inner `<app-sidesheet>` at width 100%). Regression-locked by `e2e/orchestrator-side-sheet-position.spec.ts`.

| Consumer                              | Width  |
| ------------------------------------- | ------ |
| Kanban-filter side sheet              | 320px  |
| CLI-usage side sheet                  | 440px  |
| Orchestrator side sheet               | 640px  |

**Findings.** Canonical works.

## Notification — canonical: `<app-notification>`

F37. Toast + banner layouts. Tints + borders per severity.

**Findings.** Canonical works.

## Open question — modal-padding for per-feature overlays

The four per-feature overlays (verbose-debug, orchestrator-settings, cli-usage-detail, update-center, media-lightbox) ignore `--studio-modal-padding-*`. **Decision deferred** to a follow-up task: either migrate them to `<app-dialog>` (lose the per-feature width / sizing flexibility), or have them opt into the token vocabulary while keeping their own surface. Proposal in [migration-status.md](migration-status.md).

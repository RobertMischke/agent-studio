/**
 * Shared models for the unified app dialog family:
 *  - confirm dialogs (delete / discard / load-all-style guard rails)
 *  - notification popups (success / info / warning / error)
 *
 * The full error overlay with stack trace + copy lives in
 * `error-dialog.model.ts`; this file covers the lighter-weight surfaces
 * that previously fell back to `window.confirm` and ad-hoc toasts.
 */

export type NotificationKind = 'success' | 'info' | 'warning' | 'error';

export type ConfirmDialogKind = 'danger' | 'primary';

export interface ConfirmDialogOptions {
  title: string;
  message: string;
  /** Extra body line below the message, e.g. the affected item's name. */
  detail?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Visual flavour. `danger` is the default for delete-style flows. */
  kind?: ConfirmDialogKind;
}

export interface ConfirmDialogState extends Required<Omit<ConfirmDialogOptions, 'detail'>> {
  detail: string | null;
}

export interface NotificationOptions {
  message: string;
  title?: string;
  kind: NotificationKind;
  /**
   * Auto-dismiss after this many ms. Pass `0` to keep it open until the
   * user dismisses or the caller calls `dismiss(id)`. When omitted the
   * service picks a sensible default per `kind` (errors stay longer).
   */
  durationMs?: number;
}

export interface NotificationState extends NotificationOptions {
  id: number;
  durationMs: number;
}

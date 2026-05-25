/**
 * Shared models for the unified app dialog family:
 *  - confirm dialogs (delete / discard / load-all-style guard rails)
 *  - notification popups (success / info / warning / error)
 *
 * The full error overlay with stack trace + copy lives in
 * `error-dialog.model.ts`; this file covers the lighter-weight surfaces
 * that previously fell back to `window.confirm` and ad-hoc toasts.
 */

export type NotificationKind = 'success' | 'info' | 'warning' | 'error' | 'accent';

export type ConfirmDialogKind = 'danger' | 'primary';

export interface NotificationAction {
  label: string;
  testId?: string;
  primary?: boolean;
  callback: () => void;
}

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
  /**
   * Optional small caption rendered as its own sub-line under the
   * message (typically the project / job slug that produced the
   * notification). Callers must put it here instead of concatenating
   * "... in <project>" onto `message` so the toast layout can clamp the
   * body independently of the source label.
   */
  source?: string;
  /** Optional detail lines rendered below the message (e.g. verification failures). */
  details?: string[];
  /** Action buttons rendered in the toast footer. */
  actions?: NotificationAction[];
}

export interface NotificationState extends NotificationOptions {
  id: number;
  durationMs: number;
}

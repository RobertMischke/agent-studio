import { ChangeDetectionStrategy, Component, EventEmitter, HostListener, Input, Output, ViewEncapsulation } from '@angular/core';
import { StudioIconComponent } from '../studio-icon/studio-icon.component';

/**
 * Reusable modal dialog skeleton.
 *
 * The companion to <app-sidesheet>: this owns the **modal** shape
 * (centred panel, backdrop click-to-close, alertdialog ARIA, Esc
 * cooperation with ModalStackService). The two shapes are
 * deliberately separate components — they look similar but their
 * interaction semantics differ.
 *
 * Use this for: error, confirm, create-job, media-lightbox,
 * e2e-cleanup, update-block, update-center, verbose-debug,
 * orchestrator-settings — anything that asks the user to make a
 * one-off decision against the rest of the app.
 *
 * **Not** for side panels — those use <app-sidesheet>.
 *
 * Usage:
 *
 *   <app-dialog
 *     eyebrow="Confirm delete"
 *     title="Delete this task?"
 *     role="alertdialog"
 *     (close)="onCancel()"
 *     (backdropClick)="onCancel()">
 *     <p>This removes the job folder and all its files.</p>
 *     <div footer class="my-actions">
 *       <button (click)="onCancel()">Cancel</button>
 *       <button (click)="onConfirm()">Delete</button>
 *     </div>
 *   </app-dialog>
 *
 * Backdrop click is opt-in via the `(backdropClick)` event so callers
 * keep full control over cancellation semantics — some dialogs
 * (verbose-debug, log overlays) intentionally don't close on a
 * mis-click.
 */
@Component({
  selector: 'app-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './dialog.component.html',
  styleUrl: './dialog.component.scss',
})
export class DialogComponent {
  @Input() eyebrow: string | null = null;
  @Input() title = '';
  /** Optional one-line caption under the title. */
  @Input() subtitle: string | null = null;
  /** ARIA role — `alertdialog` for confirms/errors, `dialog` for forms. */
  @Input() role: 'dialog' | 'alertdialog' = 'dialog';
  /** Optional width override (px). Default 520. */
  @Input() width: number | null = null;
  /** Hides the close button when set to `false`. */
  @Input() closable = true;
  /** Visual variant — drives accent stripe colour. */
  @Input() kind: 'default' | 'danger' | 'primary' = 'default';
  @Input() testid: string | null = null;

  @Output() readonly closeRequest = new EventEmitter<void>();
  @Output() readonly backdropClick = new EventEmitter<void>();

  onBackdropClick(): void {
    this.backdropClick.emit();
  }

  /** Pressing Esc on the dialog itself emits `close`. Outer Esc handling
   *  (ModalStackService) is the caller's concern. */
  @HostListener('keydown.escape')
  onEscape(): void { this.closeRequest.emit(); }
}

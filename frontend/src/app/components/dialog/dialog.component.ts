import { ChangeDetectionStrategy, Component, ElementRef, HostListener, OnDestroy, OnInit, ViewEncapsulation, inject, input, output } from '@angular/core';
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
export class DialogComponent implements OnInit, OnDestroy {
  readonly eyebrow = input<string | null>(null);
  readonly title = input('');
  /** Optional one-line caption under the title. */
  readonly subtitle = input<string | null>(null);
  /** ARIA role — `alertdialog` for confirms/errors, `dialog` for forms. */
  readonly role = input<'dialog' | 'alertdialog'>('dialog');
  /** Optional width override (px). Default 520. */
  readonly width = input<number | null>(null);
  /** Hides the close button when set to `false`. */
  readonly closable = input(true);
  /** Visual variant — drives accent stripe colour. */
  readonly kind = input<'default' | 'danger' | 'primary'>('default');
  /**
   * Body padding variant. `md` (default) uses --studio-modal-padding-body
   * (24px); `sm` drops to --studio-modal-padding-body-sm (16px) for confirm-
   * style dialogs that are intentionally tight. Header/footer always use
   * --studio-modal-padding-header / -footer regardless of size.
   */
  readonly size = input<'sm' | 'md'>('md');
  readonly testid = input<string | null>(null);

  /**
   * Relocate this dialog's host element to `<body>` on init.
   *
   * The overlay is `position: fixed`, but a fixed element is positioned
   * relative to the nearest ancestor that establishes a containing block
   * for fixed descendants — `transform`, `filter`, or `contain:
   * layout/paint` all do this. When the dialog renders inline inside such
   * an ancestor (e.g. the lane `.column`, which carries `contain: layout
   * paint` for scroll perf), the overlay is positioned against that tall,
   * scrolled box and then clipped off-screen by its `overflow: hidden`
   * parents instead of covering the viewport. Hosting at `<body>` escapes
   * both the containing block and the clipping. Off by default so the
   * many in-flow dialog callers are unaffected.
   */
  readonly portalToBody = input(false);

  private readonly host = inject(ElementRef).nativeElement as HTMLElement;

  ngOnInit(): void {
    if (this.portalToBody()) {
      document.body.appendChild(this.host);
    }
  }

  ngOnDestroy(): void {
    // The host was moved out of its declared @if view, so remove it
    // explicitly; Angular's own teardown reads the live parent and skips
    // the already-detached node, so there is no double removal.
    if (this.portalToBody()) {
      this.host.remove();
    }
  }

  readonly closeRequest = output<void>();
  readonly backdropClick = output<void>();

  onBackdropClick(): void {
    this.backdropClick.emit();
  }

  /** Pressing Esc on the dialog itself emits `close`. Outer Esc handling
   *  (ModalStackService) is the caller's concern. */
  @HostListener('keydown.escape')
  onEscape(): void { this.closeRequest.emit(); }
}

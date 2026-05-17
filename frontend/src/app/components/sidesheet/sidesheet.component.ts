import { ChangeDetectionStrategy, Component, Input, Output, EventEmitter, ViewEncapsulation } from '@angular/core';
import { StudioIconComponent } from '../studio-icon/studio-icon.component';

/**
 * Reusable sidesheet / dialog skeleton.
 *
 * Twelve overlay surfaces in the app shared a near-identical header /
 * body / footer skeleton with subtly diverging class names; this
 * component owns the layout, theming, close button, and slot
 * projection so call sites only have to ship their inner content.
 *
 * See docs/frontend-scss-quality.md "Wave B" for the migration plan.
 *
 * Usage:
 *
 * <app-sidesheet
 *   eyebrow="BOARD"
 *   title="Filter & view"
 *   (close)="onClose()">
 *   <ng-container body>
 *     ... your panel body ...
 *   </ng-container>
 *   <div footer class="my-footer-bits">
 *     <button>Clear</button>
 *   </div>
 * </app-sidesheet>
 *
 * Variants:
 *   - `variant="sheet"`  pinned to the right edge of the viewport
 *                         (the default — matches the legacy `.sheet`).
 *   - `variant="dialog"` centred modal panel.
 */
@Component({
  selector: 'app-sidesheet',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './sidesheet.component.html',
  styleUrl: './sidesheet.component.scss',
})
export class SidesheetComponent {
  @Input() eyebrow: string | null = null;
  @Input() title: string = '';
  @Input() variant: 'sheet' | 'dialog' = 'sheet';
  /** Optional width override (px). Sheet default = 360, dialog default = 520. */
  @Input() width: number | null = null;
  /** Hides the close button when set to `false`. */
  @Input() closable: boolean = true;
  /** data-testid passthrough for stable element selection. */
  @Input() testid: string | null = null;

  @Output() readonly close = new EventEmitter<void>();
}

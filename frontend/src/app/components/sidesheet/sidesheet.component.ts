import { ChangeDetectionStrategy, Component, Input, Output, EventEmitter, ViewEncapsulation } from '@angular/core';
import { StudioIconComponent } from '../studio-icon/studio-icon.component';

/**
 * Reusable sidesheet skeleton — strictly for **side panels** pinned to
 * the right edge of the viewport (kanban filter, CLI usage,
 * orchestrator chat, workspace screenshots, etc.). Owns the layout,
 * theming, close button, and slot projection so call sites only have
 * to ship their inner content.
 *
 * **Not** intended for modal dialogs (error / confirm / create-job /
 * media-lightbox / verbose-debug). Those have different semantics —
 * backdrop click-to-close, alertdialog ARIA role, focus trap,
 * shaped specifically for one decision. They keep their own
 * components or migrate to a future `<app-dialog>` skeleton when one
 * exists.
 *
 * See docs/frontend-scss-quality.md "Wave B" for the migration plan.
 *
 * Usage:
 *
 *   <app-sidesheet
 *     eyebrow="BOARD"
 *     title="Filter & view"
 *     (close)="onClose()">
 *     <ng-container body>
 *       ... your panel body ...
 *     </ng-container>
 *     <div footer class="my-footer-bits">
 *       <button>Clear</button>
 *     </div>
 *   </app-sidesheet>
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
  /** Optional width override (px). Default 360. */
  @Input() width: number | null = null;
  /** Hides the close button when set to `false`. */
  @Input() closable: boolean = true;
  /** data-testid passthrough for stable element selection. */
  @Input() testid: string | null = null;

  @Output() readonly close = new EventEmitter<void>();
}

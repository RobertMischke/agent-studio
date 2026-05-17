import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output, ViewEncapsulation } from '@angular/core';
import { StudioIconComponent, StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from '../../../../components/tooltip';

/**
 * Single status-bar chip: icon + label + click. Six call sites in the
 * status bar repeated the same `<button class="statusbar__item">` skeleton;
 * this component owns the layout + hover state so additions stay
 * one-line.
 *
 * The component preserves the legacy class names (`statusbar__item`,
 * `statusbar__icon`) so existing SCSS rules + Playwright selectors
 * continue to match.
 */
@Component({
  selector: 'app-statusbar-item',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent, TooltipDirective],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './statusbar-item.component.html',
  styleUrls: [],
})
export class StatusbarItemComponent {
  @Input() icon: StudioIconName | null = null;
  @Input() iconSize: number = 12;
  @Input() label: string = '';
  /** Tooltip text — uses the project's TooltipDirective. */
  @Input() tooltip: string = '';
  /** Renders as a button (default true) vs read-only text chip. */
  @Input() button: boolean = true;
  @Input() testid: string | null = null;
  /** Animate the icon as a live indicator (e.g. the "● running" chip). */
  @Input() pulsing: boolean = false;
  /** Bullet character used for read-only chips that pre-date the SVG icon
   *  set ("● running" / "↻ N/M auto"); takes precedence over `icon` so
   *  callers can keep the legacy glyph without forcing every status-bar
   *  chip to a flat SVG. */
  @Input() bullet: string | null = null;

  @Output() readonly click = new EventEmitter<MouseEvent>();
}

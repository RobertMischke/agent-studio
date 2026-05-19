import { ChangeDetectionStrategy, Component, Input, Output, EventEmitter, ViewEncapsulation } from '@angular/core';
import { StudioIconComponent, StudioIconName } from '../studio-icon/studio-icon.component';

/**
 * Compact pane header used by the prompt / protocol / git panes (and
 * any future pane that joins the detail view). Owns the icon + title +
 * actions slot + maximize / hide buttons so the three pane components
 * stop reimplementing the same chrome.
 *
 * The prompt pane's three-tab strip (Description / Evidence / Code
 * Review) renders inside the projected `tabs` slot instead of using
 * the title; pass an empty title in that case and project the tabs.
 *
 * See docs/frontend-scss-quality.md "Wave C".
 */
@Component({
  selector: 'app-pane-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './pane-header.component.html',
  styleUrl: './pane-header.component.scss',
})
export class PaneHeaderComponent {
  @Input() icon: StudioIconName | null = null;
  @Input() title = '';
  @Input() maximized = false;
  @Input() maximizable = true;
  @Input() hideable = true;
  /** Optional extra subtitle that follows the title. */
  @Input() subtitle: string | null = null;
  /** data-testid passthrough. */
  @Input() testid: string | null = null;

  @Output() readonly maximize = new EventEmitter<void>();
  @Output() readonly hide = new EventEmitter<void>();
}

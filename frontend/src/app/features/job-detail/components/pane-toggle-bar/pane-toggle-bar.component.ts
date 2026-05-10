import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { PaneName, PanesVisible } from '../../services/layout-panes.service';

/**
 * Strip of icon-only toggle buttons for the prompt / protocol / git
 * panes plus the "Open in VS Code" launch shortcut. The label sits on
 * the tooltip; the icon strip itself stays under 28 px tall so the
 * panes below get the freed pixels.
 */
@Component({
  selector: 'app-pane-toggle-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pane-toggle-bar.component.html',
  styleUrl: './pane-toggle-bar.component.scss'
})
export class PaneToggleBarComponent {
  readonly panesVisible = input.required<PanesVisible>();

  readonly toggle = output<PaneName>();
  readonly openInVsCode = output<void>();
}

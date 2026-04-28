import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { PaneName, PanesVisible } from '../layout-panes.service';

/**
 * Strip of toggle buttons for the prompt / protocol / git panes,
 * plus the "Open in VS Code" launch shortcut.
 */
@Component({
  selector: 'app-pane-toggle-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pane-toggle-bar.component.html'
})
export class PaneToggleBarComponent {
  readonly panesVisible = input.required<PanesVisible>();

  readonly toggle = output<PaneName>();
  readonly openInVsCode = output<void>();
}

import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { PaneName, PanesVisible } from '../layout-panes.service';

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
  styles: [`
    :host { display: block; }

    .detail__panes-toolbar--icons {
      display: flex;
      align-items: center;
      gap: 4px;
      padding: 2px 0 4px;
      border-bottom: 1px solid rgba(255,255,255,0.04);
      margin-bottom: 6px;
      min-height: 26px;
    }

    .detail__panes-toolbar-spacer { flex: 1; }

    .pane-toggle {
      display: inline-grid;
      place-items: center;
      width: 24px;
      height: 24px;
      padding: 0;
      border-radius: 3px;
      border: 1px solid rgba(255,255,255,0.10);
      background: rgba(255,255,255,0.04);
      color: #94a3b8;
      cursor: pointer;
      font-size: 14px;
      line-height: 1;
      transition: background 0.12s ease, border-color 0.12s ease, color 0.12s ease;
    }
    .pane-toggle:hover {
      background: rgba(255,255,255,0.10);
      color: #f1f5f9;
      border-color: rgba(255,255,255,0.22);
    }
    .pane-toggle--active {
      background: rgba(99,102,241,0.20);
      border-color: rgba(99,102,241,0.55);
      color: #c7d2fe;
    }
  `]
})
export class PaneToggleBarComponent {
  readonly panesVisible = input.required<PanesVisible>();

  readonly toggle = output<PaneName>();
  readonly openInVsCode = output<void>();
}

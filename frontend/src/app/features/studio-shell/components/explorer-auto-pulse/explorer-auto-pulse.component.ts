import { ChangeDetectionStrategy, Component, ViewEncapsulation, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { ProjectPulseState } from '../../studio-shell.pulse';

/**
 * AGT-2031 — the subtle auto-pickup pulse dot for the Explorer tree. Purely
 * presentational: a design-token-coloured dot that pulses gently while a
 * project's auto-pickup is armed but idle, and more lively (brighter colour,
 * faster cadence, a breathing dot) while a run is actually executing.
 * `prefers-reduced-motion` falls back to a static dot. The host renders with
 * `display: contents` so the inner `<span>` becomes the flex item directly —
 * on a project row the slot is always present (reserved width) so toggling auto
 * never reflows the row (requirement #4 — no layout jump).
 */
@Component({
  selector: 'app-explorer-auto-pulse',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './explorer-auto-pulse.component.html',
  styleUrl: './explorer-auto-pulse.component.scss',
})
export class ExplorerAutoPulseComponent {
  readonly state = input<ProjectPulseState>('off');
  readonly tooltip = input('');
  readonly ariaLabel = input('');
  /** data-testid stamped on the dot so callers can target a specific row. */
  readonly testid = input<string | null>(null);
  /** Collapsed workspace / tree aggregate dots get a small leading gap. */
  readonly aggregate = input(false);
}

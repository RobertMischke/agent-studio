import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';

/**
 * Compact hover popover for the status-bar quota strip. Shows only the
 * core number per CLI - primary window used%, headroom, and the current
 * window's label / reset - so a hover stays a glance, not the full
 * detail dump (that now lives in the CLI-Management panel, opened on
 * click). Purely presentational: it renders the `quotaRows` the shared
 * `CliUsageStore` already derives.
 */
@Component({
  selector: 'app-cli-usage-mini-popover',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './cli-usage-mini-popover.html',
  styleUrl: './cli-usage-mini-popover.scss',
})
export class CliUsageMiniPopoverComponent {
  readonly rows = input<CliUsageQuotaRow[]>([]);

  headroom(row: CliUsageQuotaRow): string {
    if (row.primaryPct == null) return '—';
    return `${Math.max(0, 100 - row.primaryPct)}% left`;
  }

  windowLine(row: CliUsageQuotaRow): string {
    const label = row.primary?.label ?? 'no window';
    const reset = row.primary?.resetLabel;
    return reset ? `${label} · resets ${reset}` : label;
  }
}

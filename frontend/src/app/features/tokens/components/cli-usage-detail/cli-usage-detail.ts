import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { CliType } from '../../../../models/task.model';
import { ConceptHelpComponent } from '../../../../components/concept-help/concept-help.component';
import type { QuotaWindow } from '../../../../features/quota';
import { TooltipDirective } from '../../../../components/tooltip';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type {
  AdHocUsageAggregate,
  TokenSummaryAggregate,
  TokenTimeline,
  WorkspaceExpensiveJob,
} from '../../models/tokens.model';

interface SparkPoint {
  label: string;
  total: number;
  pct: number;
}

interface ModelUsageRow {
  model: string;
  source: string;
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
}

/**
 * Full CLI-usage detail surface: routing headroom, token trend, per-CLI
 * quota windows / model spend / top sources, and the workspace's most
 * expensive tasks. Lives embedded inside the CLI-Management panel (the
 * "Settings-Dach"); the status-bar quota strip now only shows the
 * compact <app-cli-usage-mini-popover> on hover and opens this on click.
 *
 * Purely presentational - the shared `CliUsageStore` owns the polling
 * and feeds every input; `refreshAll` / `refreshOne` bubble back so the
 * host can re-probe.
 */
@Component({
  selector: 'app-cli-usage-detail',
  standalone: true,
  imports: [ConceptHelpComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './cli-usage-detail.html',
  styleUrl: './cli-usage-detail.scss',
})
export class CliUsageDetailComponent {
  readonly quotaRows = input<CliUsageQuotaRow[]>([]);
  readonly tokens = input<TokenSummaryAggregate | null>(null);
  readonly adhoc = input<AdHocUsageAggregate | null>(null);
  readonly timeline24h = input<TokenTimeline | null>(null);
  readonly timeline7d = input<TokenTimeline | null>(null);
  readonly expensiveJobs = input<WorkspaceExpensiveJob[]>([]);
  readonly refreshing = input<Record<string, boolean>>({});
  readonly refreshingAll = input(false);

  readonly refreshAll = output<Event>();
  readonly refreshOne = output<{ cliType: CliType; event: Event }>();

  headroom(row: CliUsageQuotaRow): string {
    if (row.primaryPct == null) return 'unknown';
    return `${Math.max(0, 100 - row.primaryPct)}%`;
  }

  limitText(window: QuotaWindow): string {
    if (window.used !== null && window.limit !== null) {
      return `${window.used} / ${window.limit}${window.unit ? ' ' + window.unit : ''}`;
    }
    return 'absolute values unavailable';
  }

  costLabel(value: number, priced: boolean): string {
    return priced ? this.formatUsd(value) : 'n/a';
  }

  formatTokens(n: number): string {
    if (!Number.isFinite(n)) return '0';
    if (n < 1_000) return n.toString();
    if (n < 1_000_000) return (n / 1_000).toFixed(n < 10_000 ? 1 : 0) + 'K';
    return (n / 1_000_000).toFixed(n < 10_000_000 ? 2 : 1) + 'M';
  }

  formatUsd(n: number): string {
    if (!Number.isFinite(n) || n === 0) return '$0.00';
    if (n < 0.1) return '$' + n.toFixed(4);
    if (n < 1) return '$' + n.toFixed(3);
    return '$' + n.toFixed(2);
  }

  modelRowsFor(cliType: CliType): ModelUsageRow[] {
    const rows: ModelUsageRow[] = [];
    for (const m of this.tokens()?.byModel ?? []) {
      if (!this.modelBelongsToCli(m.model, cliType)) continue;
      rows.push({
        model: m.model,
        source: 'orchestrator',
        calls: m.calls,
        inputTokens: m.inputTokens,
        outputTokens: m.outputTokens,
        cacheReadTokens: m.cacheReadTokens,
        cacheCreationTokens: m.cacheCreationTokens,
        estimatedApiCostUsd: m.estimatedApiCostUsd,
        modelPriced: m.modelPriced,
      });
    }
    for (const m of this.adhoc()?.byModel ?? []) {
      if (!this.modelBelongsToCli(m.model, cliType)) continue;
      rows.push({
        model: m.model,
        source: 'ad-hoc',
        calls: m.calls,
        inputTokens: m.inputTokens,
        outputTokens: m.outputTokens,
        cacheReadTokens: m.cacheReadTokens,
        cacheCreationTokens: m.cacheCreationTokens,
        estimatedApiCostUsd: m.estimatedApiCostUsd,
        modelPriced: m.modelPriced,
      });
    }
    return rows
      .sort((a, b) => this.totalTokens(b) - this.totalTokens(a))
      .slice(0, 5);
  }

  sourceRowsFor(cliType: CliType) {
    if (cliType !== 'claude') return [];
    return (this.adhoc()?.bySource ?? []).slice(0, 5);
  }

  topJobs(): WorkspaceExpensiveJob[] {
    return this.expensiveJobs().slice(0, 6);
  }

  spark24h(): SparkPoint[] {
    return this.sparkByBucket(this.timeline24h(), 24);
  }

  spark7d(): SparkPoint[] {
    const timeline = this.timeline7d();
    if (!timeline) return [];
    const byDay = new Map<string, number>();
    for (const cell of timeline.cells) {
      const key = (cell.bucketStart || '').slice(0, 10);
      if (!key) continue;
      byDay.set(key, (byDay.get(key) ?? 0) + cell.total);
    }
    return this.toSparkPoints(Array.from(byDay.entries()).slice(-7));
  }

  totalTokens(row: ModelUsageRow): number {
    return row.inputTokens + row.outputTokens + row.cacheReadTokens + row.cacheCreationTokens;
  }

  private sparkByBucket(timeline: TokenTimeline | null, limit: number): SparkPoint[] {
    if (!timeline) return [];
    const byBucket = new Map<string, number>();
    for (const cell of timeline.cells) {
      byBucket.set(cell.bucketStart, (byBucket.get(cell.bucketStart) ?? 0) + cell.total);
    }
    return this.toSparkPoints(Array.from(byBucket.entries()).slice(-limit));
  }

  private toSparkPoints(entries: [string, number][]): SparkPoint[] {
    const max = Math.max(1, ...entries.map(([, total]) => total));
    return entries.map(([label, total]) => ({ label, total, pct: Math.max(3, (total / max) * 100) }));
  }

  private modelBelongsToCli(model: string, cliType: CliType): boolean {
    const m = (model ?? '').toLowerCase();
    switch (cliType) {
      case 'claude':
        return m.includes('claude') || m.includes('haiku') || m.includes('sonnet') || m.includes('opus');
      case 'codex':
        return m.includes('codex') || m.startsWith('gpt') || /^o\d/.test(m);
      case 'copilot':
        return m.includes('copilot');
      case 'gemini':
        return m.includes('gemini');
      default:
        return false;
    }
  }
}

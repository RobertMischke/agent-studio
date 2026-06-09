import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TooltipDirective } from '../../../../../components/tooltip';
import { formatTokens } from '../../../../../services/format.util';
import type {
  PipelineModelTokenUsage,
  PipelineModelUsageSummary,
  PipelineRunTokenUsage,
} from '../../../../task-pipeline';

/** Column sums for the grand-total footer row (per-token-bucket + cost). */
interface GrandTotals {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  totalTokens: number;
  costUsd: number;
  anyModelUnknown: boolean;
}

/**
 * Per-model token usage for one task's RUNS, rendered under the Overview
 * Pipeline section. A single run spends tokens on several models (the core
 * agent model, the aspect reviewers' Haiku, an orchestrator decision model),
 * so a single token number per run hides where the spend went. This surface
 * breaks each run down per model and adds a visually distinct grand total
 * that sums each model over every run of the task.
 *
 * Purely presentational: the backend (`PipelineCostCalculator.SummarizeByModel`)
 * does the grouping + lifetime sum and ships it as `tokensByModel` on the
 * pipeline read endpoint. Cost is the theoretical API price (runs go through
 * CLI subscriptions, so the real bill is $0); an unpriced model renders "n/a".
 */
@Component({
  selector: 'app-pipeline-token-usage',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-token-usage.component.html',
  styleUrl: './pipeline-token-usage.component.scss',
})
export class PipelineTokenUsageComponent {
  readonly summary = input<PipelineModelUsageSummary | null>(null);

  /** Runs oldest-first (Run #1 -> latest), matching the run switcher. */
  readonly runs = computed<PipelineRunTokenUsage[]>(() => this.summary()?.runs ?? []);

  /** Per-model rollup summed over every run, busiest model first. */
  readonly totalByModel = computed<PipelineModelTokenUsage[]>(
    () => this.summary()?.totalByModel ?? [],
  );

  readonly runCount = computed<number>(() => this.runs().length);

  readonly hasMultipleRuns = computed<boolean>(() => this.runCount() > 1);

  /** Footer sums for the grand-total table, derived from the per-model rows. */
  readonly grandTotals = computed<GrandTotals>(() => {
    const models = this.totalByModel();
    return {
      inputTokens: models.reduce((a, m) => a + m.inputTokens, 0),
      outputTokens: models.reduce((a, m) => a + m.outputTokens, 0),
      cacheReadTokens: models.reduce((a, m) => a + m.cacheReadTokens, 0),
      cacheCreationTokens: models.reduce((a, m) => a + m.cacheCreationTokens, 0),
      totalTokens: this.summary()?.totalTokens ?? 0,
      costUsd: this.summary()?.totalCostUsd ?? 0,
      anyModelUnknown: this.summary()?.anyModelUnknown ?? false,
    };
  });

  tokens(n: number): string {
    return formatTokens(n);
  }

  /** Cost label; "n/a" when the model is not in the price table. */
  costLabel(usd: number, modelKnown: boolean): string {
    if (!modelKnown) return 'n/a';
    return this.formatCost(usd);
  }

  formatCost(usd: number): string {
    if (usd <= 0) return '$0.00';
    if (usd < 0.01) return `$${usd.toFixed(4)}`;
    return `$${usd.toFixed(2)}`;
  }

  relativeTime(iso: string | null | undefined): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const diffMs = Date.now() - d.getTime();
    const minutes = Math.round(diffMs / 60_000);
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.round(hours / 24);
    if (days < 30) return `${days}d ago`;
    const months = Math.round(days / 30);
    if (months < 12) return `${months}mo ago`;
    return `${Math.round(months / 12)}y ago`;
  }

  /** Per-model row tooltip: full model id + step count + token split. */
  modelTooltip(m: PipelineModelTokenUsage): string {
    const cost = m.modelKnown ? this.formatCost(m.costUsd) : 'no price on file';
    return [
      `${m.model} - ${m.steps} step(s)`,
      `Input ${this.tokens(m.inputTokens)} / Output ${this.tokens(m.outputTokens)}`,
      `Cache read ${this.tokens(m.cacheReadTokens)} / Cache write ${this.tokens(m.cacheCreationTokens)}`,
      `Total ${this.tokens(m.totalTokens)} - ${cost}`,
    ].join('\n');
  }
}

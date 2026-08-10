import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { formatTokens } from '../../../../../services/format.util';
import type {
  PipelineModelTokenUsage,
  PipelineModelUsageSummary,
  PipelinePricingGap,
  PipelineRunTokenUsage,
} from '../../../../task-pipeline';
import {
  buildTokenCostTooltip,
  formatTokenCostDisplay,
  incompleteTokenCostLabel,
} from '../../../../tokens';

/** The task-wide total (every model summed over every run). */
interface TaskTotal {
  totalTokens: number;
  costUsd: number;
  anyModelUnknown: boolean;
  unpricedRuns: number;
  pricingGaps: PipelinePricingGap[];
}

/**
 * Per-model token usage for one task's RUNS, rendered under the Overview
 * Pipeline section. A single run spends tokens on several models (the core
 * agent model, the aspect reviewers' Haiku, an orchestrator decision model),
 * so a single token number per run hides where the spend went.
 *
 * Two collapsible levels on one quiet surface (no special boxes):
 *  - TASK TOTAL SUM (primary, collapsed by default) shows the lifetime total;
 *    expanding it reveals the all-runs-by-model breakdown inline.
 *  - TOKENS BY RUN lists every run newest-first, each run collapsed by default;
 *    expanding a run reveals its per-model rows. Default-collapsed scales to
 *    dozens of runs.
 *
 * The toggle line of each level IS that level's total row, so the per-model
 * rows it discloses are literally its summands - no duplicated footer. A single
 * shared grid template keeps the numeric columns right-aligned across every
 * level (collapsed totals land in the same Total/Cost columns the breakdown
 * fills in).
 *
 * Purely presentational: the backend (`PipelineCostCalculator.SummarizeByModel`)
 * does the grouping + lifetime sum and ships it as `tokensByModel` on the
 * pipeline read endpoint. Cost is the theoretical API price (runs go through
 * CLI subscriptions, so the real bill is $0); an unpriced model renders an
 * explicit missing-price state.
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

  /** Runs newest-first for display, so the current run sits on top. */
  readonly runsNewestFirst = computed<PipelineRunTokenUsage[]>(() => [...this.runs()].reverse());

  /** Per-model rollup summed over every run, busiest model first. */
  readonly totalByModel = computed<PipelineModelTokenUsage[]>(
    () => this.summary()?.totalByModel ?? [],
  );

  readonly runCount = computed<number>(() => this.runs().length);

  /** Lifetime total over every run and model - the TASK TOTAL SUM toggle line. */
  readonly taskTotal = computed<TaskTotal>(() => ({
    totalTokens: this.summary()?.totalTokens ?? 0,
    costUsd: this.summary()?.totalCostUsd ?? 0,
    anyModelUnknown: this.summary()?.anyModelUnknown ?? false,
    unpricedRuns: this.summary()?.unpricedRuns
      ?? (this.summary()?.anyModelUnknown ? 1 : 0),
    pricingGaps: this.summary()?.pricingGaps ?? [],
  }));

  /** TASK TOTAL SUM is collapsed by default (the lifetime number is enough). */
  readonly summaryOpen = signal(false);

  /** Set of run attempts currently expanded; every run is collapsed by default. */
  private readonly openRuns = signal<ReadonlySet<number>>(new Set());

  toggleSummary(): void {
    this.summaryOpen.update((v) => !v);
  }

  isRunOpen(attempt: number): boolean {
    return this.openRuns().has(attempt);
  }

  toggleRun(attempt: number): void {
    this.openRuns.update((prev) => {
      const next = new Set(prev);
      if (next.has(attempt)) next.delete(attempt);
      else next.add(attempt);
      return next;
    });
  }

  tokens(n: number): string {
    return formatTokens(n);
  }

  costLabel(usd: number, totalTokens: number, unpricedRuns: number): string {
    return formatTokenCostDisplay({ costUsd: usd, totalTokens, unpricedRuns });
  }

  unpricedRunsForRun(run: PipelineRunTokenUsage): number {
    return run.anyModelUnknown ? 1 : 0;
  }

  unpricedRunsForModel(model: PipelineModelTokenUsage): number {
    return model.unpricedRuns ?? (model.totalTokens > 0 && !model.modelKnown ? 1 : 0);
  }

  incompleteLabel(unpricedRuns: number): string {
    return incompleteTokenCostLabel(unpricedRuns);
  }

  isPartial(costUsd: number, unpricedRuns: number): boolean {
    return costUsd > 0 && unpricedRuns > 0;
  }

  totalTooltip(
    totalTokens: number,
    costUsd: number,
    anyModelUnknown: boolean,
    scope: string,
    unpricedRuns = anyModelUnknown ? 1 : 0,
    pricingGaps: readonly PipelinePricingGap[] = [],
  ): string {
    return buildTokenCostTooltip({
      costUsd,
      priceKnown: !anyModelUnknown,
      totalTokens,
      context: `${scope}: ${totalTokens.toLocaleString()} total tokens.`,
      unpricedRuns,
      pricingGaps,
    });
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
    const context = [
      `${m.model} - ${m.steps} step(s)`,
      `Input ${this.tokens(m.inputTokens)} / Output ${this.tokens(m.outputTokens)}`,
      `Cache read ${this.tokens(m.cacheReadTokens)} / Cache write ${this.tokens(m.cacheCreationTokens)}`,
      `Total ${this.tokens(m.totalTokens)}`,
    ].join('\n');
    return buildTokenCostTooltip({
      costUsd: m.costUsd,
      priceKnown: m.modelKnown,
      totalTokens: m.totalTokens,
      context,
      unpricedRuns: this.unpricedRunsForModel(m),
      pricingGaps: m.pricingGaps,
    });
  }
}

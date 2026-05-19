import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, input, signal } from '@angular/core';
import type { TokenSummary } from '../../../../features/tokens';
import { JobService } from '../../../../services/job.service';
import { TokensApiService } from '../../../../features/tokens';

import { TooltipDirective } from '../../../../components/tooltip';
/**
 * Per-project token rollup block. Three rows:
 *
 * 1. **Amounts (real).** Total input / output / cache tokens across all
 *    orchestrator LLM calls on this project, with a per-model breakdown
 *    underneath. These numbers are accurate; they come straight from
 *    the orchestrator's CLI envelopes.
 * 2. **Theoretical API cost (estimate).** What the same tokens would
 *    have cost via Anthropic's API. Carried because it is a useful
 *    comparison and a sanity check, *not* because the user pays it.
 *    The disclaimer line is always shown.
 * 3. **Subscription quota link.** Pointer to the existing CLI Usage
 *    sheet (the 🪙 Usage button in the toolbar) which surfaces the
 *    real billing dimension - the user's Pro / Max plan windows.
 *
 * Mounted by `OrchestratorFeedComponent` (header) and
 * `ProjectDetailComponent` (group). 5s poll while mounted.
 */
@Component({
  selector: 'app-token-summary-block',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-summary-block.html',
  styleUrl: './token-summary-block.scss'
})
export class TokenSummaryBlockComponent implements OnInit, OnDestroy {
  private readonly tokensApi = inject(TokensApiService);
  readonly projectName = input.required<string>();

  private readonly jobService = inject(JobService);
  readonly summary = signal<TokenSummary | null>(null);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setInterval(() => this.refresh(), 5_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  refresh(): void {
    this.tokensApi.getTokenSummary(this.projectName()).subscribe({
      next: (s) => this.summary.set(s),
      error: () => { /* keep last value */ }
    });
  }

  /** "12,345" -> "12.3K" so the metric strip stays compact. */
  formatTokens(n: number): string {
    if (n < 1_000) return n.toString();
    if (n < 1_000_000) return (n / 1_000).toFixed(n < 10_000 ? 1 : 0) + 'K';
    return (n / 1_000_000).toFixed(n < 10_000_000 ? 2 : 1) + 'M';
  }

  /** Two decimal places below $10, three below $1, four below $0.10. */
  formatUsd(n: number): string {
    if (!Number.isFinite(n)) return '$0.00';
    if (n === 0) return '$0.00';
    if (n < 0.1) return '$' + n.toFixed(4);
    if (n < 1)   return '$' + n.toFixed(3);
    return '$' + n.toFixed(2);
  }
}

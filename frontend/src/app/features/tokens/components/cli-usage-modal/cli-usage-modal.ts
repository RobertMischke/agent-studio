import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import type { CliType } from '../../../../models/task.model';
import type { QuotaWindow } from '../../../../features/quota';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { CliUsageQuotaRow } from '../../services/cli-usage.store';
import type { AdHocUsageAggregate, TokenSummaryAggregate } from '../../models/tokens.model';
import { CostBreakdownService } from '../../services/cost-breakdown.service';

interface ModelUsageRow {
  model: string;
  source: string;
  /** OpenAI reports cached input as a subset of input, while Anthropic
   *  reports cache-read tokens as a separate category. */
  cacheIncludedInInput: boolean;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
  earliestTs?: string | null;
  latestTs?: string | null;
}

/** Oldest / most recent recorded call across the shown model rows, or all
 *  null when no row carries a usable timestamp. */
interface DataRange {
  sinceLabel: string | null;
  asOfIso: string | null;
  asOfLabel: string | null;
}

type WindowTone = 'ok' | 'warn' | 'hot' | 'unknown';

/**
 * Presentational projection of a reported quota window: the raw
 * {@link QuotaWindow} plus the derived percentage, traffic-light tone,
 * clamped bar width, and the reset / limit strings the card renders.
 * No mapping or refresh logic lives here — it only reshapes the input
 * row into what the template draws.
 */
interface WindowView {
  label: string;
  pct: number | null;
  pctLabel: string;
  remainingLabel: string | null;
  barPct: number;
  tone: WindowTone;
  /** Implied cap, or null when the window reports no usable limit. */
  limit: string | null;
  reset: string | null;
}

/** Summary head over the model table: totals shown as stat tiles. */
interface UsageTotals {
  costUsd: number;
  tokens: number;
  models: number;
  anyPriced: boolean;
  allPriced: boolean;
}

/**
 * One detail modal for a single CLI's usage. Opened by clicking that
 * CLI's card in the status-bar quota strip — one modal per CLI type, no
 * shared hover tooltip and no grouped multi-CLI view. Shows every quota
 * window the probe reported (so Claude / Codex surface both their 5h and
 * weekly windows), the plan / freshness header, this CLI's top models,
 * and any probe error. The footer drops into the full CLI-Management
 * caps surface or re-probes just this CLI.
 *
 * Purely presentational: the host (`<app-usage-hover-panel>`) owns the
 * `CliUsageStore` polling, the ModalStack registration, and the
 * open-state; this component renders the inputs and bubbles intent.
 */
@Component({
  selector: 'app-cli-usage-modal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogComponent, TooltipDirective, AppTooltipDirective],
  templateUrl: './cli-usage-modal.html',
  styleUrl: './cli-usage-modal.scss',
})
export class CliUsageModalComponent {
  private readonly costBreakdown = inject(CostBreakdownService);
  readonly cliType = input.required<CliType>();
  readonly row = input<CliUsageQuotaRow | null>(null);
  readonly tokens = input<TokenSummaryAggregate | null>(null);
  readonly adhoc = input<AdHocUsageAggregate | null>(null);
  readonly refreshing = input(false);

  readonly closeRequest = output<void>();
  readonly refresh = output<void>();
  readonly manageCaps = output<void>();

  readonly title = computed(() => this.row()?.label ?? this.cliLabel(this.cliType()));

  readonly subtitle = computed(() => {
    const r = this.row();
    if (!r) return 'No data yet';
    const parts: string[] = [r.plan ?? 'No plan reported'];
    if (r.source) parts.push(r.source);
    parts.push(r.freshness);
    return parts.join(' · ');
  });

  readonly windows = computed<QuotaWindow[]>(() => this.row()?.windows ?? []);

  /** Reshapes each reported window into its card projection (pct, tone,
   *  bar width, reset countdown). Pure derivation of the input row. */
  readonly windowViews = computed<WindowView[]>(() =>
    this.windows().map((w) => {
      const pct = w.usedPct === null ? null : Math.round(w.usedPct);
      const barPct = pct === null ? 0 : Math.max(0, Math.min(100, pct));
      const limit = this.limitText(w);
      return {
        label: w.label,
        pct,
        pctLabel: pct === null ? 'Unknown' : `${pct}% used`,
        remainingLabel: pct === null ? null : `${Math.max(0, 100 - pct)}% left`,
        barPct,
        tone: this.toneForPct(pct),
        limit: limit === 'n/a' ? null : limit,
        reset: w.resetLabel ?? null,
      };
    }),
  );

  /** Summed cost / token totals across the shown model rows — the
   *  "Summen-Kopf" over the model table. Presentational only. */
  readonly totals = computed<UsageTotals>(() => {
    const rows = this.modelRows();
    let costUsd = 0;
    let tokens = 0;
    let anyPriced = false;
    for (const r of rows) {
      tokens += this.totalTokens(r);
      if (r.modelPriced) {
        costUsd += r.estimatedApiCostUsd;
        anyPriced = true;
      }
    }
    return { costUsd, tokens, models: rows.length, anyPriced, allPriced: rows.length > 0 && rows.every(r => r.modelPriced) };
  });

  readonly modelRows = computed<ModelUsageRow[]>(() => {
    const cli = this.cliType();
    const rows: ModelUsageRow[] = [];
    for (const m of this.tokens()?.byModel ?? []) {
      if (!this.modelBelongsToCli(m.model, cli)) continue;
      const row = { ...m, source: 'project runtime', cacheIncludedInInput: cli === 'codex' };
      if (this.totalTokens(row) > 0) rows.push(row);
    }
    for (const m of this.adhoc()?.byModel ?? []) {
      if (!this.modelBelongsToCli(m.model, cli)) continue;
      const row = { ...m, source: 'ad-hoc', cacheIncludedInInput: cli === 'codex' };
      if (this.totalTokens(row) > 0) rows.push(row);
    }
    return rows.sort((a, b) => this.totalTokens(b) - this.totalTokens(a)).slice(0, 5);
  });

  /** Oldest / most recent recorded call across every shown model row
   *  (not just the top 5), so "since / as of" reflects the full lifetime
   *  telemetry the totals above are summed from, not the truncated table. */
  readonly dataRange = computed<DataRange>(() => {
    const cli = this.cliType();
    let earliest: Date | null = null;
    let latest: Date | null = null;
    const scan = (rows: { model: string; earliestTs?: string | null; latestTs?: string | null }[]) => {
      for (const row of rows) {
        if (!this.modelBelongsToCli(row.model, cli)) continue;
        if (row.earliestTs) {
          const d = new Date(row.earliestTs);
          if (!isNaN(d.getTime()) && (!earliest || d < earliest)) earliest = d;
        }
        if (row.latestTs) {
          const d = new Date(row.latestTs);
          if (!isNaN(d.getTime()) && (!latest || d > latest)) latest = d;
        }
      }
    };
    scan(this.tokens()?.byModel ?? []);
    scan(this.adhoc()?.byModel ?? []);
    if (!earliest || !latest) return { sinceLabel: null, asOfIso: null, asOfLabel: null };
    const asOfIso = (latest as Date).toISOString();
    return {
      sinceLabel: this.formatDate(earliest as Date),
      asOfIso,
      asOfLabel: this.formatTs(asOfIso),
    };
  });

  limitText(window: QuotaWindow): string {
    if (window.used !== null && window.limit !== null) {
      return `${window.used} / ${window.limit}${window.unit ? ' ' + window.unit : ''}`;
    }
    // Operator rule: a "%" window with no explicit numeric limit is capped
    // at 100% (the CLI reports "66% used", so the limit is implicitly 100%).
    // Show the implied cap instead of a bare "n/a" so Codex windows
    // (used/limit null, only usedPct) read as "66%" against "100%".
    if (window.unit === '%' && window.usedPct !== null) {
      return '100%';
    }
    return 'n/a';
  }

  costLabel(value: number, priced: boolean): string {
    return priced ? this.formatUsd(value) : 'Unknown';
  }

  totalTokens(row: ModelUsageRow): number {
    return row.inputTokens
      + row.outputTokens
      + row.cacheCreationTokens
      + (row.cacheIncludedInInput ? 0 : row.cacheReadTokens);
  }

  /** Read + creation cache tokens folded into one "Cache" column value. */
  cacheTokens(row: ModelUsageRow): number {
    return row.cacheReadTokens + row.cacheCreationTokens;
  }

  showTotalCalculation(): void {
    this.costBreakdown.show(this.modelRows().map(row => this.priceItem(row)),
      `${this.title()} recorded usage cost`);
  }

  showModelCalculation(row: ModelUsageRow): void {
    this.costBreakdown.show([this.priceItem(row)], `${row.model} cost calculation`);
  }

  private priceItem(row: ModelUsageRow) {
    return {
      model: row.model,
      label: row.source,
      inputTokens: row.inputTokens,
      outputTokens: row.outputTokens,
      cacheReadTokens: row.cacheReadTokens,
      cacheWriteTokens: row.cacheCreationTokens,
    };
  }

  private toneForPct(pct: number | null): WindowTone {
    if (pct === null) return 'unknown';
    if (pct < 70) return 'ok';
    if (pct < 90) return 'warn';
    return 'hot';
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

  private formatDate(d: Date): string {
    const yyyy = d.getUTCFullYear();
    const mm = String(d.getUTCMonth() + 1).padStart(2, '0');
    const dd = String(d.getUTCDate()).padStart(2, '0');
    return `${yyyy}-${mm}-${dd}`;
  }

  private formatTs(iso: string): string {
    const d = new Date(iso);
    if (isNaN(d.getTime())) return iso;
    const hh = String(d.getUTCHours()).padStart(2, '0');
    const mi = String(d.getUTCMinutes()).padStart(2, '0');
    return `${this.formatDate(d)} ${hh}:${mi}`;
  }

  private modelBelongsToCli(model: string, cliType: CliType): boolean {
    const m = (model ?? '').toLowerCase();
    switch (cliType) {
      case 'claude':
        return m.includes('claude') || m.includes('haiku') || m.includes('sonnet') || m.includes('opus');
      case 'codex':
        return m.includes('codex') || m.startsWith('gpt') || /^o\d/.test(m);
      case 'gemini':
        return m.includes('gemini');
      default:
        return false;
    }
  }

  private cliLabel(cli: CliType): string {
    switch (cli) {
      case 'claude': return 'Claude';
      case 'codex': return 'Codex';
      case 'gemini': return 'Gemini';
      default: return cli;
    }
  }
}

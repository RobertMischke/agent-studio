import { Component, OnDestroy, OnInit, signal, computed } from '@angular/core';
import { JobService } from '../services/job.service';
import { CliType, QuotaReport, QuotaSnapshot, QuotaWindow } from '../models/job.model';

/**
 * Compact strip surfacing each installed CLI's subscription quota / rate-limit
 * status — designed to live at the top of the CLI Sessions sidesheet so the
 * "how much budget do I have left" question is answerable at a glance.
 *
 * Behaviour:
 *  - On mount, calls `GET /api/cli/quota` which returns the cached report and
 *    triggers background re-probes for any stale entry. No spinner needed.
 *  - The "↻" buttons force a synchronous re-probe of one CLI (or all).
 *    These calls take several seconds because they spawn a fresh PTY.
 *  - Re-renders every second so reset countdowns stay live.
 *  - Bar colour: green < 70%, amber 70–90%, red > 90%.
 */
@Component({
  selector: 'app-quota-strip',
  standalone: true,
  template: `
    <section class="qstrip">
      <header class="qstrip__head">
        @if (lastUpdate(); as lu) {
          <span class="qstrip__updated">updated {{ lu }}</span>
        }
        <button class="qstrip__refresh"
                type="button"
                title="Re-probe all CLIs (slow, several seconds)"
                [disabled]="refreshingAll()"
                (click)="refreshAll()">
          {{ refreshingAll() ? '⏳' : '↻' }}
        </button>
      </header>

      @if (errorMsg(); as err) {
        <div class="qstrip__error">{{ err }}</div>
      }

      <div class="qstrip__cards">
        @for (snap of snapshots(); track snap.cliType) {
          <article class="qcard">
            <div class="qcard__head">
              <span class="qcard__cli">{{ cliLabel(snap.cliType) }}</span>
              @if (snap.plan) {
                <span class="qcard__plan">{{ snap.plan }}</span>
              }
              <button class="qcard__refresh"
                      type="button"
                      title="Re-probe {{ cliLabel(snap.cliType) }}"
                      [disabled]="refreshing()[snap.cliType]"
                      (click)="refreshOne(snap.cliType)">
                {{ refreshing()[snap.cliType] ? '⏳' : '↻' }}
              </button>
            </div>

            @if (snap.error && snap.windows.length === 0) {
              <div class="qcard__error" [title]="snap.error">No quota data available.</div>
            } @else if (snap.windows.length === 0) {
              <div class="qcard__hint">Loading…</div>
            } @else {
              @for (w of snap.windows; track w.label) {
                <div class="qwin">
                  <div class="qwin__row">
                    <span class="qwin__label">{{ w.label }}</span>
                    <span class="qwin__pct" [class]="severity(w.usedPct)">
                      {{ formatPct(w.usedPct) }}
                    </span>
                  </div>
                  <div class="qwin__bar">
                    <span class="qwin__fill"
                          [class]="severity(w.usedPct)"
                          [style.width.%]="barWidth(w.usedPct)"></span>
                  </div>
                  <div class="qwin__meta">
                    @if (w.used !== null && w.limit !== null) {
                      <span>{{ w.used }} / {{ w.limit }}{{ w.unit ? ' ' + w.unit : '' }}</span>
                    }
                    @if (resetText(w); as rt) {
                      <span class="qwin__reset">resets {{ rt }}</span>
                    }
                  </div>
                </div>
              }
            }
          </article>
        }
      </div>
    </section>
  `,
  styles: [`
    :host { display: block; }
    .qstrip {
      padding: 8px 14px 12px;
      background: transparent;
    }
    .qstrip__head {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 8px;
      min-height: 18px;
    }
    .qstrip__title {
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      color: #94a3b8;
      font-weight: 600;
    }
    .qstrip__updated {
      font-size: 10px;
      color: #64748b;
    }
    .qstrip__refresh {
      margin-left: auto;
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #cbd5e1;
      width: 22px; height: 22px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 11px;
      line-height: 1;
    }
    .qstrip__refresh:hover:not(:disabled) { background: rgba(255,255,255,0.1); }
    .qstrip__error {
      margin-bottom: 8px;
      padding: 6px 10px;
      background: rgba(244,63,94,0.1);
      border: 1px solid rgba(244,63,94,0.18);
      color: #fda4af;
      border-radius: 6px;
      font-size: 11px;
    }
    .qstrip__cards {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .qcard {
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 8px;
      background: rgba(255,255,255,0.03);
      padding: 8px 10px;
    }
    .qcard__head {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 6px;
    }
    .qcard__cli { font-weight: 600; font-size: 12px; color: #e2e8f0; }
    .qcard__plan {
      font-size: 10px;
      padding: 1px 6px;
      border-radius: 999px;
      background: rgba(99,102,241,0.16);
      color: #a5b4fc;
      letter-spacing: 0.04em;
    }
    .qcard__refresh {
      margin-left: auto;
      background: transparent;
      border: 0;
      color: #64748b;
      cursor: pointer;
      font-size: 11px;
      width: 18px; height: 18px;
      line-height: 1;
    }
    .qcard__refresh:hover:not(:disabled) { color: #cbd5e1; }
    .qcard__error { font-size: 11px; color: #fda4af; }
    .qcard__hint  { font-size: 11px; color: #64748b; font-style: italic; }
    .qwin + .qwin { margin-top: 6px; padding-top: 6px; border-top: 1px dashed rgba(255,255,255,0.05); }
    .qwin__row { display: flex; justify-content: space-between; gap: 8px; font-size: 11px; }
    .qwin__label { color: #cbd5e1; }
    .qwin__pct { font-family: var(--font-mono, monospace); font-weight: 600; }
    .qwin__pct.ok    { color: #4ade80; }
    .qwin__pct.warn  { color: #facc15; }
    .qwin__pct.crit  { color: #f87171; }
    .qwin__bar {
      margin-top: 4px;
      height: 6px;
      background: rgba(255,255,255,0.06);
      border-radius: 999px;
      overflow: hidden;
    }
    .qwin__fill {
      display: block;
      height: 100%;
      transition: width 0.4s ease;
      background: #4ade80;
    }
    .qwin__fill.warn { background: #facc15; }
    .qwin__fill.crit { background: #f87171; }
    .qwin__meta {
      margin-top: 4px;
      display: flex;
      justify-content: space-between;
      gap: 8px;
      font-size: 10px;
      color: #64748b;
    }
    .qwin__reset { font-style: italic; }
  `]
})
export class QuotaStripComponent implements OnInit, OnDestroy {
  readonly report = signal<QuotaReport | null>(null);
  readonly errorMsg = signal<string | null>(null);
  readonly refreshingAll = signal(false);
  // Per-CLI in-flight refresh flags so each card's spinner is independent.
  readonly refreshing = signal<Record<CliType, boolean>>({
    copilot: false, claude: false, codex: false
  });
  // Tick once a second so countdown labels (`resets in 23m`) stay live without
  // re-fetching the snapshot.
  private readonly nowTick = signal(Date.now());
  private tickHandle: ReturnType<typeof setInterval> | null = null;
  private autoHandle: ReturnType<typeof setInterval> | null = null;

  readonly snapshots = computed<QuotaSnapshot[]>(() => this.report()?.snapshots ?? []);
  readonly lastUpdate = computed(() => {
    const at = this.report()?.at;
    if (!at) return null;
    try { return new Date(at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }
    catch { return null; }
  });

  constructor(private jobService: JobService) {}

  ngOnInit() {
    this.load();
    this.tickHandle = setInterval(() => this.nowTick.set(Date.now()), 1000);
    // Pull a fresh cached report every 30s — backend handles its own staleness.
    this.autoHandle = setInterval(() => this.load(true), 30000);
  }

  ngOnDestroy() {
    if (this.tickHandle) clearInterval(this.tickHandle);
    if (this.autoHandle) clearInterval(this.autoHandle);
  }

  private load(silent = false) {
    if (!silent) this.errorMsg.set(null);
    this.jobService.getQuotaReport().subscribe({
      next: r => this.report.set(r),
      error: err => {
        if (!silent) this.errorMsg.set(err.error?.error || err.message || 'Failed to load quota');
      }
    });
  }

  refreshAll() {
    this.refreshingAll.set(true);
    this.errorMsg.set(null);
    this.jobService.refreshQuotaAll().subscribe({
      next: r => { this.report.set(r); this.refreshingAll.set(false); },
      error: err => {
        this.errorMsg.set(err.error?.error || err.message || 'Quota refresh failed');
        this.refreshingAll.set(false);
      }
    });
  }

  refreshOne(cliType: CliType) {
    this.refreshing.update(m => ({ ...m, [cliType]: true }));
    this.jobService.refreshQuotaForCli(cliType).subscribe({
      next: snap => {
        // Splice the updated snapshot back into the cached report.
        const cur = this.report();
        if (cur) {
          const next = {
            ...cur,
            snapshots: cur.snapshots.map(s => s.cliType === cliType ? snap : s)
          };
          this.report.set(next);
        }
        this.refreshing.update(m => ({ ...m, [cliType]: false }));
      },
      error: err => {
        this.errorMsg.set(err.error?.error || err.message || `Refresh failed for ${cliType}`);
        this.refreshing.update(m => ({ ...m, [cliType]: false }));
      }
    });
  }

  cliLabel(t: CliType): string {
    switch (t) {
      case 'copilot': return 'Copilot';
      case 'claude':  return 'Claude Code';
      case 'codex':   return 'Codex';
      case 'gemini':  return 'Gemini';
    }
  }

  formatPct(pct: number | null): string {
    if (pct === null || isNaN(pct)) return '—';
    return `${pct.toFixed(pct >= 10 ? 0 : 1)}%`;
  }

  /** Used percentage clamped to [0, 100] for the progress bar's visual width. */
  barWidth(pct: number | null): number {
    if (pct === null || isNaN(pct)) return 0;
    return Math.max(0, Math.min(100, pct));
  }

  severity(pct: number | null): 'ok' | 'warn' | 'crit' {
    if (pct === null || isNaN(pct)) return 'ok';
    if (pct >= 90) return 'crit';
    if (pct >= 70) return 'warn';
    return 'ok';
  }

  /**
   * Live human-friendly "in 1h 23m" / "in 4d 3h" countdown when we have a
   * concrete reset timestamp; otherwise falls back to whatever the backend
   * already formatted (e.g. "May 1", "3:40am (Europe/Berlin)").
   */
  resetText(w: QuotaWindow): string | null {
    if (w.resetAt) {
      // Use the ticking signal as the time base — NOT Date.now() — so the
      // value is stable within a single change-detection cycle. Reading
      // Date.now() directly causes NG0100 when the wall clock crosses a
      // minute boundary between the dev-mode "check" and "verify" passes.
      const now = this.nowTick();
      const target = Date.parse(w.resetAt);
      if (!isNaN(target)) {
        const ms = target - now;
        if (ms > 0) {
          const fallback = w.resetLabel ? ` (${w.resetLabel})` : '';
          return `in ${this.formatDuration(ms)}${fallback}`;
        }
      }
    }
    return w.resetLabel ?? null;
  }

  private formatDuration(ms: number): string {
    const sec = Math.floor(ms / 1000);
    const days = Math.floor(sec / 86400);
    const hours = Math.floor((sec % 86400) / 3600);
    const mins = Math.floor((sec % 3600) / 60);
    if (days > 0) return `${days}d ${hours}h`;
    if (hours > 0) return `${hours}h ${mins}m`;
    if (mins > 0) return `${mins}m`;
    return `${sec}s`;
  }
}

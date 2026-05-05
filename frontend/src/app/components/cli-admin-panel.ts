import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobService } from '../services/job.service';
import { CliType, QuotaReport, QuotaSnapshot, QuotaWindow } from '../models/job.model';
import { cliTypeIcon, cliTypeLabel } from '../services/format.util';

interface CapsResponse {
  defaultCapPct: number;
  caps: Record<string, Record<string, number>>;
}

interface CapRow {
  cliType: CliType;
  windowLabel: string;
  capPct: number;
  usedPct: number | null;
  // Stored separately so the slider can show a transient drag value before
  // the debounced PUT lands and the canonical caps map updates.
  pendingCapPct: number;
  saving: boolean;
}

/**
 * Admin / management surface for installed CLIs. The first capability shipped
 * here is the per-CLI per-window usage cap: each quota window from the latest
 * /api/cli/quota snapshot gets a slider that the user drags to set "do not
 * run past N% of this window". The runner gates auto-pickup and stops in-
 * flight runs when usage crosses these caps.
 *
 * Other admin / statistics content lives behind a "Coming soon" placeholder
 * - the user explicitly asked for the sliders first; the rest is on the
 *   roadmap but does not block this surface.
 */
@Component({
  selector: 'app-cli-admin-panel',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="cli-admin" data-testid="cli-admin-panel">
      <header class="cli-admin__header">
        <div>
          <h2 class="cli-admin__title">CLI Management</h2>
          <p class="cli-admin__subtitle">
            Per-CLI usage caps. The runner blocks pickup and stops in-flight
            runs when the configured percentage is reached, so manual ad-hoc
            work outside the orchestrator keeps a buffer.
          </p>
        </div>
        <button class="cli-admin__refresh"
                type="button"
                data-testid="cli-admin-refresh"
                [disabled]="loading()"
                (click)="reload()">
          {{ loading() ? '⏳' : '↻' }}
          <span>Reload</span>
        </button>
      </header>

      @if (errorMsg(); as err) {
        <div class="cli-admin__error" data-testid="cli-admin-error">{{ err }}</div>
      }

      <section class="cli-admin__section">
        <header class="cli-admin__section-head">
          <h3>Usage caps</h3>
          <span class="cli-admin__hint">default {{ defaultCapPct() }}%</span>
        </header>

        @if (rows().length === 0) {
          <div class="cli-admin__empty">
            No quota windows reported yet. Open the CLI Usage panel and
            refresh to populate quota data.
          </div>
        }

        @for (cli of cliGroups(); track cli.cliType) {
          <article class="cli-card" [attr.data-cli]="cli.cliType">
            <header class="cli-card__head">
              <span class="cli-card__icon" aria-hidden="true">{{ icon(cli.cliType) }}</span>
              <span class="cli-card__name">{{ label(cli.cliType) }}</span>
              @if (cli.plan) {
                <span class="cli-card__plan">{{ cli.plan }}</span>
              }
            </header>

            @for (row of cli.rows; track row.windowLabel) {
              <div class="cap-row" [attr.data-window]="row.windowLabel">
                <div class="cap-row__head">
                  <span class="cap-row__label">{{ row.windowLabel }}</span>
                  <span class="cap-row__used"
                        [class.cap-row__used--blocked]="row.usedPct !== null && row.usedPct >= row.pendingCapPct">
                    used
                    <strong>{{ formatPct(row.usedPct) }}</strong>
                  </span>
                </div>
                <div class="cap-row__slider">
                  <input type="range"
                         [attr.data-testid]="'cap-slider-' + row.cliType + '-' + slugify(row.windowLabel)"
                         min="50" max="100" step="1"
                         [ngModel]="row.pendingCapPct"
                         (ngModelChange)="onSliderChange(row, $event)"
                         (change)="onSliderCommit(row)"
                         [disabled]="row.saving"
                         [attr.aria-label]="'Cap percent for ' + label(row.cliType) + ' ' + row.windowLabel" />
                  <span class="cap-row__cap">
                    cap <strong [attr.data-testid]="'cap-value-' + row.cliType + '-' + slugify(row.windowLabel)">{{ row.pendingCapPct }}%</strong>
                    @if (row.saving) { <span class="cap-row__saving">saving…</span> }
                  </span>
                </div>
                @if (row.usedPct !== null) {
                  <div class="cap-row__bar">
                    <span class="cap-row__bar-fill"
                          [style.width.%]="barWidth(row.usedPct)"
                          [class.cap-row__bar-fill--over]="row.usedPct >= row.pendingCapPct"></span>
                    <span class="cap-row__bar-marker"
                          [style.left.%]="barWidth(row.pendingCapPct)"
                          aria-hidden="true"></span>
                  </div>
                }
              </div>
            }
          </article>
        }
      </section>

      <section class="cli-admin__section cli-admin__section--placeholder">
        <header class="cli-admin__section-head">
          <h3>Statistics</h3>
          <span class="cli-admin__hint">coming soon</span>
        </header>
        <p class="cli-admin__placeholder">
          Token spend, run counts, quota-cap blocks per day. Placeholder for now.
        </p>
      </section>

      <section class="cli-admin__section cli-admin__section--placeholder">
        <header class="cli-admin__section-head">
          <h3>Diagnostics</h3>
          <span class="cli-admin__hint">coming soon</span>
        </header>
        <p class="cli-admin__placeholder">
          CLI version reports, PATH resolution, last quota probe error. Placeholder.
        </p>
      </section>
    </div>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .cli-admin {
      padding: 16px 18px 24px;
      color: #cdd6f4;
      max-width: 720px;
    }
    .cli-admin__header {
      display: flex; align-items: flex-start; gap: 14px;
      margin-bottom: 16px;
    }
    .cli-admin__title { margin: 0 0 4px; font-size: 18px; font-weight: 700; color: #f5f5fa; }
    .cli-admin__subtitle { margin: 0; font-size: 12px; color: #94a3b8; max-width: 56ch; line-height: 1.45; }
    .cli-admin__refresh {
      margin-left: auto;
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.10);
      color: #cdd6f4;
      padding: 4px 10px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 11px;
      display: inline-flex; align-items: center; gap: 6px;
    }
    .cli-admin__refresh:hover:not(:disabled) { background: rgba(255,255,255,0.10); }
    .cli-admin__refresh:disabled { opacity: 0.6; cursor: not-allowed; }
    .cli-admin__error {
      background: rgba(244,63,94,0.10);
      border: 1px solid rgba(244,63,94,0.25);
      color: #fda4af;
      padding: 8px 12px;
      border-radius: 6px;
      font-size: 12px;
      margin-bottom: 12px;
    }
    .cli-admin__section { margin-bottom: 22px; }
    .cli-admin__section-head {
      display: flex; align-items: baseline; gap: 8px;
      margin-bottom: 8px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      padding-bottom: 4px;
    }
    .cli-admin__section-head h3 {
      margin: 0; font-size: 11px; letter-spacing: 0.10em;
      text-transform: uppercase; color: #94a3b8; font-weight: 600;
    }
    .cli-admin__hint { font-size: 10px; color: #64748b; }
    .cli-admin__empty {
      font-size: 12px; color: #94a3b8;
      padding: 12px 14px;
      border: 1px dashed rgba(255,255,255,0.08);
      border-radius: 8px;
    }
    .cli-card {
      border: 1px solid rgba(255,255,255,0.06);
      background: rgba(255,255,255,0.02);
      border-radius: 10px;
      padding: 12px 14px;
      margin-bottom: 10px;
    }
    .cli-card__head {
      display: flex; align-items: center; gap: 8px;
      padding-bottom: 8px;
      border-bottom: 1px dashed rgba(255,255,255,0.05);
      margin-bottom: 10px;
    }
    .cli-card__icon { font-size: 16px; }
    .cli-card__name { font-weight: 600; font-size: 13px; color: #e2e8f0; }
    .cli-card__plan {
      font-size: 10px;
      padding: 1px 6px;
      border-radius: 999px;
      background: rgba(99,102,241,0.16);
      color: #a5b4fc;
      letter-spacing: 0.04em;
    }
    .cap-row + .cap-row { margin-top: 10px; padding-top: 10px; border-top: 1px dashed rgba(255,255,255,0.05); }
    .cap-row__head { display: flex; justify-content: space-between; gap: 12px; font-size: 12px; }
    .cap-row__label { color: #cbd5e1; font-weight: 500; }
    .cap-row__used { color: #94a3b8; font-family: var(--font-mono, monospace); }
    .cap-row__used strong { color: #e2e8f0; font-weight: 600; }
    .cap-row__used--blocked strong { color: #fda4af; }
    .cap-row__slider {
      display: grid;
      grid-template-columns: 1fr auto;
      align-items: center;
      gap: 12px;
      margin-top: 6px;
    }
    .cap-row__slider input[type="range"] {
      width: 100%;
      accent-color: #a78bfa;
    }
    .cap-row__cap {
      font-size: 11px;
      color: #94a3b8;
      font-family: var(--font-mono, monospace);
      min-width: 80px;
      text-align: right;
    }
    .cap-row__cap strong { color: #c4b5fd; font-weight: 600; }
    .cap-row__saving { margin-left: 6px; color: #facc15; font-style: italic; }
    .cap-row__bar {
      position: relative;
      height: 6px;
      margin-top: 6px;
      background: rgba(255,255,255,0.06);
      border-radius: 999px;
      overflow: visible;
    }
    .cap-row__bar-fill {
      position: absolute;
      top: 0; left: 0; bottom: 0;
      background: #4ade80;
      border-radius: 999px;
      transition: width 0.4s ease;
    }
    .cap-row__bar-fill--over { background: #f87171; }
    .cap-row__bar-marker {
      position: absolute;
      top: -2px;
      width: 2px;
      height: 10px;
      background: #c4b5fd;
      transform: translateX(-1px);
    }
    .cli-admin__section--placeholder .cli-admin__placeholder {
      margin: 0;
      padding: 10px 14px;
      font-size: 12px;
      color: #64748b;
      background: rgba(255,255,255,0.02);
      border: 1px dashed rgba(255,255,255,0.06);
      border-radius: 8px;
    }
  `]
})
export class CliAdminPanelComponent implements OnInit, OnDestroy {
  private readonly jobService = inject(JobService);

  readonly loading = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly defaultCapPct = signal(95);
  readonly caps = signal<Record<string, Record<string, number>>>({});
  readonly report = signal<QuotaReport | null>(null);

  readonly rows = computed<CapRow[]>(() => {
    const r = this.report();
    if (!r) return [];
    const caps = this.caps();
    const def = this.defaultCapPct();
    const out: CapRow[] = [];
    for (const snap of r.snapshots) {
      for (const w of snap.windows) {
        if (!w.label) continue;
        const cap = caps[snap.cliType]?.[w.label] ?? def;
        out.push({
          cliType: snap.cliType as CliType,
          windowLabel: w.label,
          capPct: cap,
          usedPct: w.usedPct,
          pendingCapPct: cap,
          saving: false
        });
      }
    }
    return out;
  });

  // Local mutable mirror of the rows so slider drags do not have to fight a
  // recomputation. `rows()` seeds `localRows` on every backend update; the
  // template binds to `localRows` so the slider thumb tracks the user's drag
  // even mid-PUT.
  readonly localRows = signal<CapRow[]>([]);

  readonly cliGroups = computed(() => {
    const map = new Map<CliType, { cliType: CliType; plan: string | null; rows: CapRow[] }>();
    const r = this.report();
    const planByCli = new Map<string, string | null>();
    if (r) for (const s of r.snapshots) planByCli.set(s.cliType, s.plan ?? null);

    for (const row of this.localRows()) {
      let g = map.get(row.cliType);
      if (!g) {
        g = { cliType: row.cliType, plan: planByCli.get(row.cliType) ?? null, rows: [] };
        map.set(row.cliType, g);
      }
      g.rows.push(row);
    }
    return Array.from(map.values());
  });

  private autoHandle: ReturnType<typeof setInterval> | null = null;
  private debounceHandles = new Map<string, ReturnType<typeof setTimeout>>();

  ngOnInit(): void {
    this.reload();
    // Refresh quota every 60s so the visible "used %" stays current. Caps
    // themselves rarely change so we only re-fetch caps on explicit reload.
    this.autoHandle = setInterval(() => this.refreshQuotaOnly(), 60000);
  }

  ngOnDestroy(): void {
    if (this.autoHandle) clearInterval(this.autoHandle);
    for (const handle of this.debounceHandles.values()) clearTimeout(handle);
  }

  reload() {
    this.loading.set(true);
    this.errorMsg.set(null);
    let pending = 2;
    const finish = () => {
      pending--;
      if (pending === 0) {
        this.loading.set(false);
        // Mirror computed rows into localRows so the slider has its own
        // mutable copy that survives drag without re-derivation.
        this.localRows.set(this.rows().map(r => ({ ...r })));
      }
    };
    this.jobService.getQuotaCaps().subscribe({
      next: (resp: CapsResponse) => {
        this.defaultCapPct.set(resp.defaultCapPct);
        this.caps.set(resp.caps ?? {});
        finish();
      },
      error: err => {
        this.errorMsg.set(this.errorMessage(err, 'Failed to load caps'));
        finish();
      }
    });
    this.jobService.getQuotaReport().subscribe({
      next: r => { this.report.set(r); finish(); },
      error: err => {
        this.errorMsg.set(this.errorMessage(err, 'Failed to load quota'));
        finish();
      }
    });
  }

  private refreshQuotaOnly() {
    this.jobService.getQuotaReport().subscribe({
      next: r => {
        this.report.set(r);
        // Update used% on existing local rows without disturbing pendingCapPct.
        const computed = this.rows();
        const existing = new Map(this.localRows().map(r => [r.cliType + '|' + r.windowLabel, r]));
        const merged = computed.map(c => {
          const prev = existing.get(c.cliType + '|' + c.windowLabel);
          return prev
            ? { ...c, pendingCapPct: prev.pendingCapPct, saving: prev.saving }
            : c;
        });
        this.localRows.set(merged);
      },
      error: () => { /* silent on background tick */ }
    });
  }

  onSliderChange(row: CapRow, val: number) {
    const clamped = Math.max(50, Math.min(100, Math.round(Number(val) || 0)));
    this.localRows.update(rows =>
      rows.map(r =>
        r.cliType === row.cliType && r.windowLabel === row.windowLabel
          ? { ...r, pendingCapPct: clamped }
          : r
      )
    );
    // Also debounce-save while the user drags so slow drags persist before
    // commit; the change-event saves on release for fast clicks.
    this.scheduleSave(row.cliType, row.windowLabel, clamped);
  }

  onSliderCommit(row: CapRow) {
    const current = this.localRows().find(r =>
      r.cliType === row.cliType && r.windowLabel === row.windowLabel)?.pendingCapPct;
    if (current === undefined) return;
    this.scheduleSave(row.cliType, row.windowLabel, current, /*immediate*/ true);
  }

  private scheduleSave(cliType: CliType, windowLabel: string, capPct: number, immediate = false) {
    const key = cliType + '|' + windowLabel;
    const existing = this.debounceHandles.get(key);
    if (existing) clearTimeout(existing);
    const fire = () => {
      this.debounceHandles.delete(key);
      this.saveNow(cliType, windowLabel, capPct);
    };
    if (immediate) fire();
    else this.debounceHandles.set(key, setTimeout(fire, 350));
  }

  private saveNow(cliType: CliType, windowLabel: string, capPct: number) {
    this.localRows.update(rows =>
      rows.map(r =>
        r.cliType === cliType && r.windowLabel === windowLabel
          ? { ...r, saving: true }
          : r
      )
    );
    this.jobService.setQuotaCap(cliType, windowLabel, capPct).subscribe({
      next: (resp: CapsResponse) => {
        this.defaultCapPct.set(resp.defaultCapPct);
        this.caps.set(resp.caps ?? {});
        this.localRows.update(rows =>
          rows.map(r =>
            r.cliType === cliType && r.windowLabel === windowLabel
              ? { ...r, saving: false, capPct }
              : r
          )
        );
      },
      error: err => {
        this.errorMsg.set(this.errorMessage(err, 'Failed to save cap'));
        this.localRows.update(rows =>
          rows.map(r =>
            r.cliType === cliType && r.windowLabel === windowLabel
              ? { ...r, saving: false }
              : r
          )
        );
      }
    });
  }

  formatPct(pct: number | null): string {
    if (pct === null || isNaN(pct)) return '—';
    return `${pct.toFixed(pct >= 10 ? 0 : 1)}%`;
  }

  barWidth(pct: number | null): number {
    if (pct === null || isNaN(pct)) return 0;
    return Math.max(0, Math.min(100, pct));
  }

  icon(t: CliType): string { return cliTypeIcon(t); }
  label(t: CliType): string { return cliTypeLabel(t); }

  slugify(label: string): string {
    return label.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');
  }

  private errorMessage(err: any, fallback: string): string {
    return err?.error?.error || err?.message || fallback;
  }
}

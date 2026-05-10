import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobService } from '../../../services/job.service';
import type { CliType } from '../../../models/job.model';
import type { QuotaReport, QuotaSnapshot, QuotaWindow } from '../../../features/quota';
import { cliTypeIcon, cliTypeLabel } from '../../../services/format.util';
import { QuotaApiService } from '../../../features/quota';

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
  templateUrl: './cli-admin-panel.html',
  styleUrl: './cli-admin-panel.scss'
})
export class CliAdminPanelComponent implements OnInit, OnDestroy {
  private readonly quotaApi = inject(QuotaApiService);
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
    this.quotaApi.getQuotaCaps().subscribe({
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
    this.quotaApi.getQuotaReport().subscribe({
      next: r => { this.report.set(r); finish(); },
      error: err => {
        this.errorMsg.set(this.errorMessage(err, 'Failed to load quota'));
        finish();
      }
    });
  }

  private refreshQuotaOnly() {
    this.quotaApi.getQuotaReport().subscribe({
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
    this.quotaApi.setQuotaCap(cliType, windowLabel, capPct).subscribe({
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

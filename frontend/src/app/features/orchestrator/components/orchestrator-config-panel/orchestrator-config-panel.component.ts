import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { OrchestratorConfigOption, OrchestratorConfigService } from '../../../../services/orchestrator-config.service';

interface OptionGroup {
  name: string;
  options: OrchestratorConfigOption[];
}

/**
 * Drawer-style overlay (mirrors UpdateCenterComponent shape) that
 * renders the orchestrator + supervisor flag catalog as a grouped
 * list of toggles / number inputs / enum dropdowns. Writes go to
 * `appsettings.Local.json` via PUT and require a backend restart;
 * the drawer surfaces that obligation with a sticky banner.
 *
 * Reads stay open (the backend gates writes via X-Client-Id, the
 * frontend's interceptor stamps every request).
 */
@Component({
  selector: 'app-orchestrator-config-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-config-panel.component.html',
  styleUrl: './orchestrator-config-panel.component.scss'
})
export class OrchestratorConfigPanelComponent {
  readonly config = inject(OrchestratorConfigService);

  readonly open = signal(false);
  readonly snapshot = this.config.snapshot;
  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);
  readonly pending = signal<Record<string, boolean | number | string>>({});

  readonly groups = computed<OptionGroup[]>(() => {
    const snap = this.snapshot();
    if (!snap) return [];
    const order = ['Orchestrator', 'Supervisor', 'Auto-Intervention'];
    const buckets = new Map<string, OrchestratorConfigOption[]>();
    for (const opt of snap.options) {
      const list = buckets.get(opt.group) ?? [];
      list.push(opt);
      buckets.set(opt.group, list);
    }
    return order
      .filter(name => buckets.has(name))
      .map(name => ({ name, options: buckets.get(name)! }));
  });

  readonly hasPending = computed(() => Object.keys(this.pending()).length > 0);
  readonly pendingCount = computed(() => Object.keys(this.pending()).length);

  async openPanel(): Promise<void> {
    this.open.set(true);
    this.saveError.set(null);
    this.pending.set({});
    await this.config.load();
  }

  close(): void {
    this.open.set(false);
    this.pending.set({});
    this.saveError.set(null);
  }

  discard(): void {
    this.pending.set({});
  }

  onChange(opt: OrchestratorConfigOption, value: boolean | number | string): void {
    const next = { ...this.pending() };
    if (this.equals(opt.currentValue, value)) {
      delete next[opt.key];
    } else {
      next[opt.key] = value;
    }
    this.pending.set(next);
  }

  async save(): Promise<void> {
    if (this.saving()) return;
    const values = this.pending();
    if (Object.keys(values).length === 0) return;
    this.saving.set(true);
    this.saveError.set(null);
    try {
      await this.config.update(values);
      this.pending.set({});
    } catch (err: unknown) {
      this.saveError.set(this.describe(err));
    } finally {
      this.saving.set(false);
    }
  }

  asBool(v: unknown): boolean { return v === true || v === 'true'; }
  asInt(v: unknown): number {
    const n = Number(v);
    return Number.isFinite(n) ? Math.trunc(n) : 0;
  }
  formatDefault(opt: OrchestratorConfigOption): string {
    if (opt.defaultValue === null || opt.defaultValue === undefined) return '—';
    return String(opt.defaultValue);
  }

  private equals(a: unknown, b: unknown): boolean {
    if (typeof a === 'number' || typeof b === 'number') {
      return Number(a) === Number(b);
    }
    return a === b;
  }

  private describe(err: unknown): string {
    if (err && typeof err === 'object') {
      const e = err as { error?: { error?: string }; message?: string };
      if (e.error?.error) return e.error.error;
      if (e.message) return e.message;
    }
    return 'Save failed';
  }
}

import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { OrchestratorConfigOption, OrchestratorConfigService } from '../../services/orchestrator-config.service';

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
  template: `
    @if (open()) {
      <div class="ocp__backdrop"
           data-testid="orch-config-backdrop"
           (click)="close()"></div>
      <aside class="ocp"
             data-testid="orch-config-panel"
             role="dialog"
             aria-label="Orchestrator config">
        <header class="ocp__head">
          <h2 class="ocp__title">Orchestrator config</h2>
          <button type="button"
                  class="ocp__close"
                  data-testid="orch-config-close"
                  (click)="close()"
                  aria-label="Close">×</button>
        </header>

        <p class="ocp__lede">
          Hosted-service lifecycle and supervisor policy flags. All
          changes write to <code>{{ snapshot()?.overrideFilePath }}</code>
          and require a backend restart to take effect.
        </p>

        @if (config.pendingRestart()) {
          <div class="ocp__banner ocp__banner--warn"
               data-testid="orch-config-restart-banner">
            <strong>Restart required.</strong>
            New values are in <code>appsettings.Local.json</code> but the
            running backend still uses the old ones. Restart the API
            (e.g. <code>./api.sh restart</code>) to apply.
          </div>
        }

        @if (config.loadError(); as err) {
          <div class="ocp__banner ocp__banner--err"
               data-testid="orch-config-load-error">
            <strong>Could not load config:</strong> {{ err }}
          </div>
        }

        @if (saveError(); as err) {
          <div class="ocp__banner ocp__banner--err"
               data-testid="orch-config-save-error">
            <strong>Save failed:</strong> {{ err }}
          </div>
        }

        @for (group of groups(); track group.name) {
          <section class="ocp__group" [attr.data-testid]="'orch-config-group-' + group.name.toLowerCase()">
            <h3 class="ocp__group-title">{{ group.name }}</h3>
            @for (opt of group.options; track opt.key) {
              <div class="ocp__row" [attr.data-testid]="'orch-config-row-' + opt.key">
                <div class="ocp__row-main">
                  <label class="ocp__row-label">{{ opt.label }}</label>
                  <p class="ocp__row-desc">{{ opt.description }}</p>
                  <p class="ocp__row-meta">
                    <code>{{ opt.key }}</code>
                    <span class="ocp__dim"> &middot; default: {{ formatDefault(opt) }}</span>
                    @if (opt.hasOverride) {
                      <span class="ocp__pill ocp__pill--ovr">override</span>
                    }
                  </p>
                </div>
                <div class="ocp__row-control">
                  @switch (opt.type) {
                    @case ('bool') {
                      <label class="ocp__toggle">
                        <input type="checkbox"
                               [attr.data-testid]="'orch-config-input-' + opt.key"
                               [checked]="asBool(pending()[opt.key] ?? opt.currentValue)"
                               (change)="onChange(opt, ($any($event.target)).checked)">
                        <span>{{ asBool(pending()[opt.key] ?? opt.currentValue) ? 'On' : 'Off' }}</span>
                      </label>
                    }
                    @case ('int') {
                      <input type="number"
                             class="ocp__num"
                             [attr.data-testid]="'orch-config-input-' + opt.key"
                             [value]="pending()[opt.key] ?? opt.currentValue"
                             (input)="onChange(opt, asInt(($any($event.target)).value))">
                    }
                    @case ('enum') {
                      <select class="ocp__sel"
                              [attr.data-testid]="'orch-config-input-' + opt.key"
                              [value]="pending()[opt.key] ?? opt.currentValue"
                              (change)="onChange(opt, ($any($event.target)).value)">
                        @for (choice of opt.enumOptions ?? []; track choice) {
                          <option [value]="choice">{{ choice }}</option>
                        }
                      </select>
                    }
                  }
                </div>
              </div>
            }
          </section>
        }

        <footer class="ocp__foot">
          <button type="button"
                  class="ocp__btn"
                  data-testid="orch-config-save"
                  [disabled]="!hasPending() || saving()"
                  (click)="save()">
            @if (saving()) { Saving… }
            @else if (hasPending()) { Save {{ pendingCount() }} change{{ pendingCount() === 1 ? '' : 's' }} }
            @else { No changes }
          </button>
          @if (hasPending()) {
            <button type="button"
                    class="ocp__btn ocp__btn--ghost"
                    data-testid="orch-config-discard"
                    (click)="discard()">Discard</button>
          }
        </footer>
      </aside>
    }
  `,
  styles: [`
    .ocp__backdrop {
      position: fixed;
      inset: 0;
      background: rgba(0, 0, 0, 0.45);
      z-index: 200;
    }
    .ocp {
      position: fixed;
      top: 0;
      right: 0;
      bottom: 0;
      width: min(520px, 96vw);
      background: #161624;
      color: #cdd6f4;
      border-left: 1px solid rgba(255, 255, 255, 0.08);
      z-index: 201;
      overflow-y: auto;
      padding: 1rem 1.25rem 5rem;
      display: flex;
      flex-direction: column;
      gap: 1rem;
      font-size: 0.875rem;
    }
    .ocp__head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      border-bottom: 1px solid rgba(255, 255, 255, 0.08);
      padding-bottom: 0.5rem;
    }
    .ocp__title { font-size: 1rem; font-weight: 600; margin: 0; }
    .ocp__close {
      background: transparent;
      border: none;
      color: inherit;
      font-size: 1.5rem;
      cursor: pointer;
      line-height: 1;
    }
    .ocp__lede { color: rgba(205, 214, 244, 0.7); margin: 0; line-height: 1.4; }
    .ocp__banner {
      padding: 0.55rem 0.75rem;
      border-radius: 4px;
      font-size: 0.8125rem;
      line-height: 1.4;
    }
    .ocp__banner--warn { background: rgba(249, 226, 175, 0.16); color: #f9e2af; border: 1px solid rgba(249, 226, 175, 0.35); }
    .ocp__banner--err  { background: rgba(243, 139, 168, 0.18); color: #f38ba8; border: 1px solid rgba(243, 139, 168, 0.35); }
    .ocp__group {
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      border-top: 1px solid rgba(255, 255, 255, 0.06);
      padding-top: 0.75rem;
    }
    .ocp__group-title {
      font-size: 0.75rem;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      color: rgba(205, 214, 244, 0.6);
      margin: 0;
    }
    .ocp__row {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 0.75rem;
      padding: 0.5rem 0;
      align-items: center;
    }
    .ocp__row-label { font-weight: 600; }
    .ocp__row-desc { margin: 0.15rem 0 0.25rem; color: rgba(205, 214, 244, 0.7); }
    .ocp__row-meta { margin: 0; font-size: 0.75rem; color: rgba(205, 214, 244, 0.55); }
    .ocp__dim { color: rgba(205, 214, 244, 0.45); }
    .ocp__pill {
      display: inline-block;
      margin-left: 0.4rem;
      padding: 0 0.35rem;
      border-radius: 3px;
      font-size: 0.7rem;
      background: rgba(137, 180, 250, 0.18);
      color: #89b4fa;
    }
    .ocp__pill--ovr { background: rgba(166, 227, 161, 0.18); color: #a6e3a1; }
    .ocp__toggle { display: inline-flex; align-items: center; gap: 0.4rem; cursor: pointer; }
    .ocp__num, .ocp__sel {
      background: rgba(255, 255, 255, 0.06);
      color: inherit;
      border: 1px solid rgba(255, 255, 255, 0.12);
      border-radius: 3px;
      padding: 0.2rem 0.4rem;
      font-size: 0.8125rem;
      width: 6.5rem;
    }
    .ocp__foot {
      position: sticky;
      bottom: 0;
      background: linear-gradient(180deg, rgba(22, 22, 36, 0) 0%, rgba(22, 22, 36, 0.95) 30%, #161624 100%);
      padding: 0.75rem 0 0.25rem;
      display: flex;
      gap: 0.5rem;
      margin-top: auto;
    }
    .ocp__btn {
      padding: 0.5rem 0.85rem;
      border-radius: 4px;
      border: 1px solid rgba(137, 180, 250, 0.4);
      background: rgba(137, 180, 250, 0.18);
      color: inherit;
      cursor: pointer;
      font-size: 0.875rem;
    }
    .ocp__btn:hover:not(:disabled) { background: rgba(137, 180, 250, 0.28); }
    .ocp__btn:disabled { cursor: not-allowed; opacity: 0.5; }
    .ocp__btn--ghost {
      border-color: rgba(255, 255, 255, 0.18);
      background: transparent;
      color: rgba(205, 214, 244, 0.85);
    }
  `]
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

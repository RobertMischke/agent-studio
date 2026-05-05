import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { SupervisorService } from '../services/supervisor.service';
import {
  MetaCycleActionKind,
  MetaCycleConfigDto,
  MetaCycleReport,
  MetaCycleVerdict,
} from '../models/supervisor.model';

/**
 * Project-level meta-cycle panel: shows whether the per-project pause-inspect-resume
 * loop is enabled, the last cycle's verdict + action, and the trailing history of
 * cycle reports. Read-only in this first cut: the panel reflects what the
 * `MetaCycleHostedService` writes to disk via `/api/supervisor/{project}/meta-cycle`.
 *
 * The full design is in `docs/mockups/orchestrator-meta-cycle/` and ADR-0022.
 *
 * Polls every 10 s while mounted.
 */
@Component({
  selector: 'app-project-meta-cycle-section',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="proj-detail__group" data-testid="project-meta-cycle-section">
      <h3>
        <span class="pmc__icon">🔁</span>
        Meta-cycle
        <span class="pmc__pill"
              [class.pmc__pill--good]="enabled() && lastVerdict() === 'healthy'"
              [class.pmc__pill--warn]="enabled() && lastVerdict() === 'fixTriggering'"
              [class.pmc__pill--bad]="enabled() && lastVerdict() === 'escalationOnly'"
              [class.pmc__pill--off]="!enabled()"
              data-testid="project-meta-cycle-status">
          {{ statusLabel() }}
        </span>
      </h3>

      @if (loading() && reports().length === 0) {
        <p class="proj-detail__empty">Loading…</p>
      } @else if (!enabled()) {
        <p class="proj-detail__empty" data-testid="project-meta-cycle-disabled">
          The meta-cycle is off. Enable it via <code>Supervisor:MetaCycleEnabled</code>
          in <code>backend/appsettings.Local.json</code> and restart the backend, then
          opt this project in via per-project settings. Off by default; see
          <code>docs/mockups/orchestrator-meta-cycle/</code> for the full design.
        </p>
      } @else if (reports().length === 0) {
        <p class="proj-detail__empty">
          No cycles yet. The next pause + inspection runs after
          <code>{{ config()?.cycleLengthN ?? 2 }}</code> jobs reach
          <code>4-auto-review</code> or <code>5-human-review</code>.
        </p>
      } @else {
        <dl class="pmc__meta">
          <div><dt>Cycle length</dt><dd>N = {{ config()?.cycleLengthN ?? 2 }}</dd></div>
          <div><dt>Last cycle</dt><dd>{{ formatRelative(lastCompletedAt()) }} &middot; {{ verdictLabel(lastVerdict()) }}</dd></div>
          <div><dt>Last action</dt><dd><code>{{ actionLabel(lastActionKind()) }}</code> &middot; {{ lastReason() }}</dd></div>
          <div><dt>Auto-fix budget</dt><dd>{{ config()?.maxFixesPerHour ?? 2 }}/hour</dd></div>
        </dl>

        <h4 class="pmc__sub">History (last {{ reports().length }})</h4>
        <ul class="pmc__list" data-testid="project-meta-cycle-history">
          @for (r of reports(); track r.cycleId) {
            <li class="pmc__item"
                [class.pmc__item--healthy]="r.verdict === 'healthy'"
                [class.pmc__item--warn]="r.verdict === 'fixTriggering'"
                [class.pmc__item--high]="r.verdict === 'escalationOnly'">
              <span class="pmc__sev">{{ verdictLabel(r.verdict) }}</span>
              <span class="pmc__action"><code>{{ actionLabel(r.action.kind) }}</code></span>
              <span class="pmc__msg">{{ r.action.reason }}</span>
              <span class="pmc__ts">{{ formatTimeShort(r.completedAt) }}</span>
            </li>
          }
        </ul>

        @if (lastFindings().length > 0) {
          <h4 class="pmc__sub">Last cycle findings</h4>
          <ul class="pmc__list" data-testid="project-meta-cycle-findings">
            @for (f of lastFindings(); track f.topic) {
              <li class="pmc__item"
                  [class.pmc__item--warn]="f.severity === 'Warn'"
                  [class.pmc__item--high]="f.severity === 'High'">
                <span class="pmc__sev">{{ f.severity }}</span>
                <span class="pmc__action">{{ f.topic }}</span>
                <span class="pmc__msg">{{ f.message }}</span>
              </li>
            }
          </ul>
        }
      }

      <div class="pmc__actions">
        <button class="pmc__btn"
                data-testid="project-meta-cycle-refresh"
                (click)="refresh()">Refresh</button>
      </div>
    </section>
  `,
  styles: [`
    :host { display: block; }
    .pmc__icon { margin-right: 6px; }
    .pmc__pill { font-size: 0.7rem; padding: 1px 8px; border-radius: 999px; background: rgba(255,255,255,0.10); color: #cdd6f4; margin-left: 8px; text-transform: uppercase; letter-spacing: 0.04em; }
    .pmc__pill--good { background: rgba(166,227,161,0.18); color: #a6e3a1; }
    .pmc__pill--warn { background: rgba(249,226,175,0.20); color: #f9e2af; }
    .pmc__pill--bad  { background: rgba(243,139,168,0.18); color: #f38ba8; }
    .pmc__pill--off  { background: rgba(255,255,255,0.06); color: rgba(255,255,255,0.55); }
    .pmc__meta { display: grid; grid-template-columns: max-content 1fr; gap: 4px 12px; margin: 0 0 12px; font-size: 0.82rem; }
    .pmc__meta > div { display: contents; }
    .pmc__meta dt { color: rgba(255,255,255,0.55); }
    .pmc__meta dd { margin: 0; color: #cdd6f4; }
    .pmc__sub { font-size: 0.78rem; color: rgba(255,255,255,0.65); margin: 14px 0 6px; text-transform: uppercase; letter-spacing: 0.04em; }
    .pmc__list { list-style: none; padding: 0; margin: 0; }
    .pmc__item { display: grid; grid-template-columns: 80px 160px 1fr max-content; gap: 8px; padding: 6px 8px; border-left: 2px solid rgba(255,255,255,0.10); margin-bottom: 4px; font-size: 0.78rem; background: rgba(255,255,255,0.03); border-radius: 0 4px 4px 0; }
    .pmc__item--healthy { border-left-color: #a6e3a1; }
    .pmc__item--warn { border-left-color: #f9e2af; }
    .pmc__item--high { border-left-color: #f38ba8; background: rgba(243,139,168,0.06); }
    .pmc__sev { color: rgba(255,255,255,0.70); font-weight: 600; text-transform: uppercase; font-size: 0.70rem; }
    .pmc__action { color: #89b4fa; font-family: ui-monospace, monospace; }
    .pmc__msg { color: #cdd6f4; word-break: break-word; }
    .pmc__ts { color: rgba(255,255,255,0.50); font-variant-numeric: tabular-nums; }
    .pmc__actions { display: flex; gap: 6px; margin-top: 14px; flex-wrap: wrap; }
    .pmc__btn { background: rgba(255,255,255,0.06); color: #cdd6f4; border: 1px solid rgba(255,255,255,0.12); border-radius: 5px; padding: 4px 10px; font-size: 0.8rem; cursor: pointer; }
    .pmc__btn:hover { background: rgba(255,255,255,0.10); }
  `]
})
export class ProjectMetaCycleSectionComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly svc = inject(SupervisorService);
  private timer?: ReturnType<typeof setInterval>;

  readonly enabled = signal<boolean>(false);
  readonly config = signal<MetaCycleConfigDto | null>(null);
  readonly reports = signal<MetaCycleReport[]>([]);
  readonly loading = signal<boolean>(false);

  readonly lastReport = computed<MetaCycleReport | null>(() => this.reports()[0] ?? null);
  readonly lastVerdict = computed<MetaCycleVerdict | null>(() => this.lastReport()?.verdict ?? null);
  readonly lastActionKind = computed<MetaCycleActionKind | null>(() => this.lastReport()?.action.kind ?? null);
  readonly lastReason = computed<string>(() => this.lastReport()?.action.reason ?? '');
  readonly lastCompletedAt = computed<string | null>(() => this.lastReport()?.completedAt ?? null);
  readonly lastFindings = computed(() => this.lastReport()?.findings ?? []);

  readonly statusLabel = computed<string>(() => {
    if (!this.enabled()) return 'off';
    const v = this.lastVerdict();
    if (v == null) return 'idle';
    return this.verdictLabel(v);
  });

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(), 10_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
  }

  refresh(): void {
    const project = this.projectName();
    if (!project) return;
    this.loading.set(true);
    this.svc.metaCycle(project, 8).subscribe({
      next: (resp) => {
        this.enabled.set(resp.enabled);
        this.config.set(resp.config);
        // Endpoint already returns newest-first.
        this.reports.set(resp.reports);
        this.loading.set(false);
      },
      error: () => { this.loading.set(false); },
    });
  }

  verdictLabel(v: MetaCycleVerdict | null): string {
    switch (v) {
      case 'healthy': return 'healthy';
      case 'fixTriggering': return 'fix queued';
      case 'escalationOnly': return 'escalated';
      case 'aborted': return 'aborted';
      default: return 'idle';
    }
  }

  actionLabel(k: MetaCycleActionKind | null): string {
    switch (k) {
      case 'resume': return 'resume';
      case 'updateStableThenResume': return 'update-stable + resume';
      case 'queueFix': return 'queue-fix';
      case 'escalateToUser': return 'escalate-to-user';
      case 'noOp': return 'no-op';
      default: return '';
    }
  }

  formatRelative(iso: string | null): string {
    if (!iso) return '—';
    const t = Date.parse(iso);
    if (isNaN(t)) return iso;
    const seconds = Math.max(0, Math.floor((Date.now() - t) / 1000));
    if (seconds < 60) return `${seconds}s ago`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
    return `${Math.floor(seconds / 3600)}h ago`;
  }

  formatTimeShort(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleTimeString();
    } catch {
      return iso;
    }
  }
}

import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { SupervisorService } from '../../../services/supervisor.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';
import {
  SupervisorAdvisory,
  SupervisorIntervention,
  SupervisorObservation,
} from '../../../models/supervisor.model';
import { ConceptHelpComponent } from '../../../components/concept-help/concept-help.component';

/**
 * Project-level Supervisor panel: live observation snapshot + recent
 * advisories + recent interventions + manual emergency-primitive buttons.
 *
 * Polls every 5 s (observation + recent events) while mounted. Stops on
 * destroy. Buttons prompt for a reason, then call the supervisor API; on
 * success they refresh.
 *
 * First cut: no charts, no severity filter, no per-advisory acknowledge.
 * The point is to get the supervisor visible on the project page so the
 * cooperative-signal default is not invisible.
 */
@Component({
  selector: 'app-project-supervisor-section',
  standalone: true,
  imports: [FormsModule, ConceptHelpComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="proj-detail__group" data-testid="project-supervisor-section">
      <h3>
        <span class="psup__icon">🛡️</span>
        Supervisor
        <app-concept-help concept="supervisor" />
        @if (status() === 'live') {
          <span class="psup__pill psup__pill--good" data-testid="project-supervisor-status">live</span>
        } @else if (status() === 'idle') {
          <span class="psup__pill" data-testid="project-supervisor-status">idle</span>
        } @else if (status() === 'paused') {
          <span class="psup__pill psup__pill--warn" data-testid="project-supervisor-status">paused</span>
        } @else if (status() === 'error') {
          <span class="psup__pill psup__pill--bad" data-testid="project-supervisor-status">error</span>
        }
      </h3>

      @if (loading() && !observation()) {
        <p class="proj-detail__empty">Loading…</p>
      } @else if (observation()) {
        <dl class="psup__meta">
          <div><dt>Runner</dt><dd>{{ observation()!.runnerStatus }}</dd></div>
          @if (observation()!.currentJobId) {
            <div><dt>Active job</dt><dd>{{ observation()!.currentJobId }} · state {{ observation()!.currentRunState || '—' }}</dd></div>
          }
          @if (observation()!.lastProgressAt) {
            <div><dt>Last progress</dt><dd>{{ formatRelative(observation()!.lastProgressAt) }}</dd></div>
          }
          @if (observation()!.quota) {
            <div><dt>Quota ({{ observation()!.quota!.cli }})</dt><dd>{{ (observation()!.quota!.usedFraction * 100).toFixed(0) }}% used</dd></div>
          }
          <div><dt>Errors / hour</dt><dd>cli {{ observation()!.errorCounts.cliErrorsLastHour }} · orch {{ observation()!.errorCounts.orchestratorErrorsLastHour }} · failures {{ observation()!.errorCounts.runFailuresLastHour }}</dd></div>
        </dl>

        <div class="psup__actions" data-testid="project-supervisor-actions">
          <button data-testid="project-supervisor-cancel-run"
                  [disabled]="!observation()!.currentJobId"
                  (click)="onCancelRun()">Cancel run</button>
          <button data-testid="project-supervisor-pause"
                  (click)="onPause()">Pause pickup</button>
          <button data-testid="project-supervisor-force-fail"
                  [disabled]="!observation()!.currentJobId"
                  (click)="onForceFail()">Force fail</button>
          <button data-testid="project-supervisor-resume"
                  (click)="onResume()">Resume</button>
        </div>

        <h4 class="psup__sub">Recent advisories ({{ advisories().length }})</h4>
        @if (advisories().length === 0) {
          <p class="proj-detail__empty">None.</p>
        } @else {
          <ul class="psup__list" data-testid="project-supervisor-advisories">
            @for (a of advisories(); track a.createdAt + a.topic) {
              <li class="psup__item"
                  [class.psup__item--high]="a.severity === 'High'"
                  [class.psup__item--warn]="a.severity === 'Warn'">
                <span class="psup__sev">{{ a.severity }}</span>
                <span class="psup__topic">{{ a.topic }}</span>
                <span class="psup__msg">{{ a.message }}</span>
              </li>
            }
          </ul>
        }

        <h4 class="psup__sub">Recent interventions ({{ interventions().length }})</h4>
        @if (interventions().length === 0) {
          <p class="proj-detail__empty">None.</p>
        } @else {
          <ul class="psup__list" data-testid="project-supervisor-interventions">
            @for (i of interventions(); track i.createdAt + i.kind) {
              <li class="psup__item">
                <span class="psup__sev">{{ i.kind }}</span>
                <span class="psup__msg">{{ i.reason }}</span>
                @if (i.jobId) { <span class="psup__topic">{{ i.jobId }}</span> }
              </li>
            }
          </ul>
        }
      } @else {
        <p class="proj-detail__empty">No supervisor data yet for this project.</p>
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .psup__icon { margin-right: 6px; }
    .psup__pill { font-size: 0.7rem; padding: 1px 8px; border-radius: 999px; background: rgba(255,255,255,0.10); color: #cdd6f4; margin-left: 8px; }
    .psup__pill--good { background: rgba(166,227,161,0.18); color: #a6e3a1; }
    .psup__pill--warn { background: rgba(249,226,175,0.18); color: #f9e2af; }
    .psup__pill--bad  { background: rgba(243,139,168,0.18); color: #f38ba8; }
    .psup__meta { display: grid; grid-template-columns: max-content 1fr; gap: 4px 12px; margin: 0 0 12px; font-size: 0.82rem; }
    .psup__meta > div { display: contents; }
    .psup__meta dt { color: rgba(255,255,255,0.55); }
    .psup__meta dd { margin: 0; color: #cdd6f4; }
    .psup__actions { display: flex; gap: 6px; margin-bottom: 14px; flex-wrap: wrap; }
    .psup__actions button { background: rgba(255,255,255,0.06); color: #cdd6f4; border: 1px solid rgba(255,255,255,0.12); border-radius: 5px; padding: 4px 10px; font-size: 0.8rem; cursor: pointer; }
    .psup__actions button:hover:not(:disabled) { background: rgba(203,166,247,0.20); border-color: rgba(203,166,247,0.40); }
    .psup__actions button:disabled { opacity: 0.4; cursor: not-allowed; }
    .psup__sub { font-size: 0.78rem; color: rgba(255,255,255,0.65); margin: 14px 0 6px; text-transform: uppercase; letter-spacing: 0.04em; }
    .psup__list { list-style: none; padding: 0; margin: 0; }
    .psup__item { display: grid; grid-template-columns: 70px 110px 1fr; gap: 8px; padding: 6px 8px; border-left: 2px solid rgba(255,255,255,0.10); margin-bottom: 4px; font-size: 0.78rem; background: rgba(255,255,255,0.03); border-radius: 0 4px 4px 0; }
    .psup__item--warn { border-left-color: #f9e2af; }
    .psup__item--high { border-left-color: #f38ba8; background: rgba(243,139,168,0.06); }
    .psup__sev { color: rgba(255,255,255,0.70); font-weight: 600; }
    .psup__topic { color: #89b4fa; font-family: ui-monospace, monospace; }
    .psup__msg { color: #cdd6f4; word-break: break-word; }
  `]
})
export class ProjectSupervisorSectionComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly svc = inject(SupervisorService);
  private timer?: VisibleIntervalHandle;

  readonly observation = signal<SupervisorObservation | null>(null);
  readonly advisories = signal<SupervisorAdvisory[]>([]);
  readonly interventions = signal<SupervisorIntervention[]>([]);
  readonly loading = signal<boolean>(false);
  readonly status = signal<'live' | 'idle' | 'paused' | 'error' | 'unknown'>('unknown');

  ngOnInit(): void {
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(), 5000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearVisibleInterval(this.timer);
  }

  private refresh(): void {
    const project = this.projectName();
    if (!project) return;
    this.loading.set(true);
    this.svc.observe(project).subscribe({
      next: o => {
        this.observation.set(o);
        this.status.set(this.deriveStatus(o));
        this.loading.set(false);
      },
      error: () => { this.status.set('error'); this.loading.set(false); }
    });
    this.svc.recentEvents(project, 20).subscribe({
      next: e => {
        this.advisories.set([...e.advisories].reverse());
        this.interventions.set([...e.interventions].reverse());
      },
      error: () => { /* keep last good values */ }
    });
  }

  private deriveStatus(o: SupervisorObservation): 'live' | 'idle' | 'paused' | 'error' | 'unknown' {
    const r = (o.runnerStatus || '').toLowerCase();
    if (r.includes('paused')) return 'paused';
    if (o.currentJobId) return 'live';
    if (r.includes('manual') || r.includes('idle') || r.includes('auto')) return 'idle';
    return 'unknown';
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

  onCancelRun(): void {
    const o = this.observation();
    if (!o?.currentJobId) return;
    const reason = prompt('Reason to cancel the running CLI?');
    if (!reason) return;
    this.svc.cancelRun(o.project, o.currentJobId, reason).subscribe({
      next: () => this.refresh(),
      error: () => this.status.set('error')
    });
  }

  onPause(): void {
    const o = this.observation();
    if (!o) return;
    const reason = prompt('Reason to pause job pickup?');
    if (!reason) return;
    this.svc.pausePickup(o.project, reason).subscribe({
      next: () => this.refresh(),
      error: () => this.status.set('error')
    });
  }

  onForceFail(): void {
    const o = this.observation();
    if (!o?.currentJobId) return;
    const reason = prompt('Reason to force-fail this job?');
    if (!reason) return;
    this.svc.forceFail(o.project, o.currentJobId, reason).subscribe({
      next: () => this.refresh(),
      error: () => this.status.set('error')
    });
  }

  onResume(): void {
    const o = this.observation();
    if (!o) return;
    const reason = prompt('Reason to resume?');
    if (!reason) return;
    this.svc.resume(o.project, reason).subscribe({
      next: () => this.refresh(),
      error: () => this.status.set('error')
    });
  }
}

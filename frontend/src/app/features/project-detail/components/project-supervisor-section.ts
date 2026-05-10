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
  templateUrl: './project-supervisor-section.html',
  styleUrl: './project-supervisor-section.scss'
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

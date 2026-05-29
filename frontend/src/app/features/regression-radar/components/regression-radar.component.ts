import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  signal,
  OnInit,
  OnDestroy,
} from '@angular/core';
import type { RegressionRadarResult, SpecChangeEntry } from '../models/regression-radar.model';
import { TaskService } from '../../../services/task.service';
import { TooltipDirective } from '../../../components/tooltip';

@Component({
  selector: 'app-regression-radar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './regression-radar.component.html',
  styleUrl: './regression-radar.component.scss',
})
export class RegressionRadarComponent implements OnInit, OnDestroy {
  readonly jobId = input.required<string>();
  readonly watchPath = input<string>();

  private readonly jobService = inject(TaskService);
  private refreshTimer: ReturnType<typeof setInterval> | null = null;

  readonly result = signal<RegressionRadarResult | null>(null);
  readonly loading = signal(true);
  readonly expanded = signal<string | null>(null);

  readonly hasData = computed(() => {
    const r = this.result();
    return r !== null && !r.error && r.totalSpecChanges > 0;
  });

  readonly isEmpty = computed(() => {
    const r = this.result();
    return r !== null && !r.error && r.totalSpecChanges === 0;
  });

  readonly statusIcon = computed(() => {
    const r = this.result();
    if (!r || r.error || r.totalSpecChanges === 0) return null;
    if (r.driftCount > 0) return { icon: 'drift', label: 'Drift detected', tone: 'danger' };
    if (r.atRiskCount > 0) return { icon: 'review', label: 'Review needed', tone: 'warning' };
    return { icon: 'ok', label: 'All intended', tone: 'success' };
  });

  ngOnInit(): void {
    this.load();
    this.refreshTimer = setInterval(() => this.load(), 30_000);
  }

  ngOnDestroy(): void {
    if (this.refreshTimer) clearInterval(this.refreshTimer);
  }

  load(): void {
    this.jobService.getRegressionRadar(this.jobId(), this.watchPath()).subscribe({
      next: (r) => { this.result.set(r); this.loading.set(false); },
      error: () => { this.loading.set(false); },
    });
  }

  toggleExpand(path: string): void {
    this.expanded.update(v => v === path ? null : path);
  }

  categoryIcon(entry: SpecChangeEntry): string {
    switch (entry.category) {
      case 'Intended': return 'intended';
      case 'AtRisk':   return 'at-risk';
      case 'Drift':    return 'drift';
      default:         return '';
    }
  }

  categoryLabel(entry: SpecChangeEntry): string {
    switch (entry.category) {
      case 'Intended': return 'Intended';
      case 'AtRisk':   return 'At Risk';
      case 'Drift':    return 'Drift';
      default:         return entry.category;
    }
  }

  statusLabel(status: string): string {
    switch (status) {
      case 'A': return 'added';
      case 'D': return 'deleted';
      case 'M': return 'modified';
      default:
        if (status.startsWith('R')) return 'renamed';
        return status;
    }
  }
}

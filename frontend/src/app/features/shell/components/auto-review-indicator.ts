import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject } from '@angular/core';
import { AutoReviewStatusStore } from '../../../services/auto-review-status.store';

import { TooltipDirective } from '../../../components/tooltip';
@Component({
  selector: 'app-auto-review-indicator',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './auto-review-indicator.html',
  styleUrl: './auto-review-indicator.scss'
})
export class AutoReviewIndicatorComponent implements OnInit, OnDestroy {
  private readonly statusStore = inject(AutoReviewStatusStore);
  private readonly staleMs = 90_000;

  readonly status = this.statusStore.status;

  readonly tone = computed<'active' | 'idle' | 'stale'>(() => {
    const s = this.status();
    if (!s?.lastTickAt) return 'stale';
    if (s.currentJob) return 'active';
    const age = Date.now() - Date.parse(s.lastTickAt);
    return age > this.staleMs ? 'stale' : 'idle';
  });

  readonly label = computed(() => {
    const s = this.status();
    if (!s?.lastTickAt) return 'Auto-review starting';
    if (s.currentJob) return 'Auto-review running';
    const pending = s.pending ?? 0;
    if (pending > 0) return `Auto-review ${pending} queued`;
    return 'Auto-review idle';
  });

  readonly tooltip = computed(() => {
    const s = this.status();
    if (!s) return 'Auto-review status has not loaded yet.';
    if (!s.lastTickAt) return 'Auto-review has not completed its first tick since backend start.';
    const tick = new Date(s.lastTickAt).toLocaleString();
    const current = s.currentJob ? `\nCurrent: ${s.currentProject ?? 'unknown project'} / ${s.currentJob}` : '';
    return `Auto-review last tick: ${tick}\n` +
      `Candidates: ${s.pending ?? 0}\n` +
      `Accept: ${s.accept}, reissue: ${s.reissue}, escalate: ${s.escalate}, aspects: ${s.aspectsRun}` +
      current;
  });

  ngOnInit(): void {
    this.statusStore.subscribe();
  }

  ngOnDestroy(): void {
    this.statusStore.release();
  }
}

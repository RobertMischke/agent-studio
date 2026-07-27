import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

interface ProjectSettingsLite {
  maxParallelism?: number;
  executionLocation?: string;
}

/**
 * ADR-0052 per-project max parallelism (1 = sequential), for LOCAL execution.
 *
 * DEPRECATED for remote execution (AGT-2302 / AGT-2376): a remotely executed
 * project takes its concurrency from the host ceiling under Settings ->
 * Execution Hosts, and this value only seeds it during migration. It is not
 * dead, though: the local ProjectRunner still limits itself by it through
 * ParallelSlotPolicy, so removing the control left an active setting with no way
 * to change it. The card therefore renders for locally executing projects only.
 * Remove after 2026-10-01, when the CAR rework of local execution replaces it.
 */
@Component({
  selector: 'app-parallel-execution-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './parallel-execution-card.html',
  styleUrl: './parallel-execution-card.scss',
})
export class ParallelExecutionCardComponent implements OnInit {
  readonly projectName = input.required<string>();

  private readonly http = inject(HttpClient);

  readonly maxParallelism = signal<number>(1);
  readonly parallelOptions = [1, 2, 3, 4];
  /** 'local' or a runner id. Decides whether this setting applies at all. */
  readonly executionLocation = signal<string>('local');
  readonly executesLocally = computed(() => this.executionLocation() === 'local');

  ngOnInit(): void {
    this.http.get<Record<string, ProjectSettingsLite>>('/api/projects/settings').subscribe({
      next: (all) => {
        const project = all?.[this.projectName()];
        const configured = project?.maxParallelism;
        if (typeof configured === 'number' && configured >= 1) this.maxParallelism.set(configured);
        // Unknown placement counts as local: hiding a setting that is still in
        // force is exactly the bug this card was restored for.
        this.executionLocation.set(project?.executionLocation ?? 'local');
      },
      error: () => { /* stays at the sequential default, assumed local */ },
    });
  }

  /** PUT /api/projects/{name}/max-parallelism; applies to the local runner live. */
  setMaxParallelism(value: number): void {
    const slots = Math.max(1, Math.floor(value || 1));
    this.maxParallelism.set(slots);
    this.http
      .put(`/api/projects/${encodeURIComponent(this.projectName())}/max-parallelism`, { maxParallelism: slots })
      .subscribe({ next: () => { /* applied live */ }, error: () => { /* surfaced on next load */ } });
  }
}

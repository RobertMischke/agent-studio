import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';

import { FormsModule } from '@angular/forms';
import { JobInfo } from '../../../../../models/job.model';
import { CodeReviewListEntry, JobService } from '../../../../../services/job.service';

import { TooltipDirective } from '../../../../../components/tooltip';
/**
 * User-triggered code-review panel that lives in the protocol pane.
 *
 * <p>Three jobs:</p>
 * <ul>
 *   <li>Render the existing <code>code-review-*.md</code> artifacts as a
 *       newest-first list with verdict chip + one-line summary; rows
 *       expand to show the MD body inline.</li>
 *   <li>Drive the "Run Code Review" action: model picker (defaults from
 *       AppSettings via the configurable backend default), Run button
 *       that POSTs and shows a spinner until the response arrives.</li>
 *   <li>Cover the user's "Progress an die Karte, dass da gerade eine
 *       Code-Review läuft" by surfacing the running indicator inside the
 *       detail pane (the spinner stays visible for the whole CLI call).
 *       A card-level badge is a future follow-up.</li>
 * </ul>
 *
 * <p>Architecturally the panel mirrors {@link ReviewEvidencePanelComponent}:
 * presentational where possible, with a small amount of local-only fetch
 * state so the parent does not have to manage every lifecycle event.</p>
 */
@Component({
  selector: 'app-code-review-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, TooltipDirective],
  templateUrl: './code-review-panel.component.html',
  styleUrl: './code-review-panel.component.scss',
})
export class CodeReviewPanelComponent implements OnInit {
  readonly job = input.required<JobInfo>();
  /** Optional override for the model dropdown's initial value. */
  readonly defaultModel = input<string>('claude-opus-4-7');

  readonly entries = signal<CodeReviewListEntry[]>([]);
  readonly loading = signal(true);
  readonly running = signal(false);
  readonly error = signal<string | null>(null);
  readonly expandedFile = signal<string | null>(null);
  readonly expandedBody = signal<string | null>(null);
  readonly selectedModel = signal<string>('claude-opus-4-7');

  /**
   * Reasonable Claude model menu. Kept narrow on purpose: the user asked
   * for a configurable default, not a full model catalogue. A future slice
   * can wire {@link CliService}'s model catalogue endpoint here when it
   * becomes useful.
   */
  readonly availableModels: readonly { id: string; label: string }[] = [
    { id: 'claude-opus-4-7', label: 'Opus 4.7 (default)' },
    { id: 'claude-sonnet-4-6', label: 'Sonnet 4.6' },
    { id: 'claude-haiku-4-5-20251001', label: 'Haiku 4.5' },
  ];

  private readonly jobs = inject(JobService);

  /** True when there is at least one MD listed and the user can drill in. */
  readonly hasEntries = computed(() => this.entries().length > 0);

  ngOnInit(): void {
    this.selectedModel.set(this.defaultModel());
    this.refresh();
  }

  /** Re-pull the listing. Public so the parent can call it after a run. */
  refresh(): void {
    const job = this.job();
    if (!job?.id) return;
    this.loading.set(true);
    this.error.set(null);
    this.jobs.listCodeReviews(job.id, job.watchPath).subscribe({
      next: (resp) => {
        this.entries.set(resp.entries ?? []);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.message ?? 'Failed to load code reviews.');
        this.loading.set(false);
      },
    });
  }

  /**
   * Trigger one review pass against the job's most recent commit. The
   * request body intentionally omits <code>commit</code> so the backend
   * resolves HEAD at request time. The button stays disabled and the
   * spinner stays up until the POST resolves.
   */
  runReview(): void {
    const job = this.job();
    if (!job?.id) return;
    if (this.running()) return;
    this.running.set(true);
    this.error.set(null);
    this.jobs.runCodeReview(job.id, { model: this.selectedModel() }, job.watchPath).subscribe({
      next: () => {
        this.running.set(false);
        this.refresh();
      },
      error: (err) => {
        this.error.set(err?.message ?? 'Code review failed.');
        this.running.set(false);
      },
    });
  }

  /** Toggle the inline body view for one row. */
  toggle(entry: CodeReviewListEntry): void {
    if (this.expandedFile() === entry.fileName) {
      this.expandedFile.set(null);
      this.expandedBody.set(null);
      return;
    }
    this.expandedFile.set(entry.fileName);
    this.expandedBody.set(null);
    const job = this.job();
    if (!job?.id) return;
    this.jobs.readCodeReview(job.id, entry.fileName, job.watchPath).subscribe({
      next: (resp) => {
        if (this.expandedFile() === entry.fileName) {
          this.expandedBody.set(resp.content);
        }
      },
      error: () => {
        if (this.expandedFile() === entry.fileName) {
          this.expandedBody.set('Failed to load review body.');
        }
      },
    });
  }

  verdictTone(v: string): 'pass' | 'concerns' | 'block' | 'unknown' {
    const lower = (v ?? '').toLowerCase();
    if (lower === 'pass') return 'pass';
    if (lower === 'concerns') return 'concerns';
    if (lower === 'block') return 'block';
    return 'unknown';
  }

  trackByFile(_index: number, entry: CodeReviewListEntry): string {
    return entry.fileName;
  }

  formatTimestamp(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toISOString().replace('T', ' ').slice(0, 16) + 'Z';
    } catch {
      return iso;
    }
  }
}

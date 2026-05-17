import { ChangeDetectionStrategy, Component, ViewChild, computed, inject, input, output, signal } from '@angular/core';
import { MarkdownRichEditorComponent } from '../../../../components/markdown-rich-editor';
import { JobInfo, JobPromptHistoryEntry, JobTitleHistoryEntry, ReviewEvidenceEntry } from '../../../../models/job.model';
import { markdownToHtml } from '../../../../components/markdown-utils';
import { MarkdownImageLightboxDirective } from '../../../../directives/markdown-image-lightbox.directive';
import { resolveProtocolImageSrc } from '../protocol-pane/protocol-image-resolver';
import { ReviewEvidencePanelComponent } from '../protocol-pane/review-evidence-panel.component';
import { JobService } from '../../../../services/job.service';

import { TooltipDirective } from '../../../../components/tooltip';
/**
 * Description / Evidence pane of the job-detail view. Hosts the markdown
 * prompt editor in the Description tab and the review-evidence panel
 * (findings from security audits, code-review passes, task checks, human
 * notes) in the Evidence tab — matches the reference detail design
 * (.reference-layout/detail.jsx) where Description and Evidence are
 * sibling tabs in the left pane. The Evidence tab badge surfaces the
 * count of non-acknowledged findings so the user notices a new entry
 * without expanding the panel.
 */
@Component({
  selector: 'app-prompt-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownRichEditorComponent, MarkdownImageLightboxDirective, TooltipDirective, ReviewEvidencePanelComponent],
  templateUrl: './prompt-pane.component.html',
  styleUrls: ['./prompt-pane.component.scss']
})
export class PromptPaneComponent {
  readonly markdown = input<string>('');
  readonly history = input<JobPromptHistoryEntry[]>([]);
  readonly titleHistory = input<JobTitleHistoryEntry[]>([]);
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  /** Evidence entries for the Evidence tab; default empty. */
  readonly evidence = input<ReviewEvidenceEntry[]>([]);
  /** Owning job info — required for the embedded evidence panel. */
  readonly job = input<JobInfo | null>(null);

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();
  readonly save = output<string>();
  /** Fires after a successful acknowledge or follow-up so the parent can refresh. */
  readonly evidenceMutated = output<void>();

  private readonly jobs = inject(JobService);
  @ViewChild('evidencePanel') private evidencePanel?: { clearBusy(): void };

  readonly activeTab = signal<'description' | 'evidence'>('description');

  readonly evidenceBadge = computed<number>(() =>
    this.evidence().filter(e => !e.acknowledged).length,
  );

  selectTab(tab: 'description' | 'evidence'): void {
    this.activeTab.set(tab);
  }

  /**
   * Mirrors protocol-pane.onEvidenceAcknowledge: hit the acknowledge API
   * for the given evidence row, clear the panel's busy spinner on
   * settled, and bubble the mutation up to the parent so it can refresh
   * the JobDetail snapshot.
   */
  onEvidenceAcknowledge(payload: { entry: ReviewEvidenceEntry; acknowledged: boolean }): void {
    const job = this.job();
    if (!job) {
      this.evidencePanel?.clearBusy();
      return;
    }
    this.jobs
      .acknowledgeReviewEvidence(job.id, payload.entry.id, payload.acknowledged, job.watchPath)
      .subscribe({
        next: () => {
          this.evidencePanel?.clearBusy();
          this.evidenceMutated.emit();
        },
        error: () => this.evidencePanel?.clearBusy(),
      });
  }

  onEvidenceCreateFollowup(entry: ReviewEvidenceEntry): void {
    const job = this.job();
    if (!job) {
      this.evidencePanel?.clearBusy();
      return;
    }
    this.jobs
      .createReviewEvidenceFollowup(job.id, entry.id, {}, job.watchPath)
      .subscribe({
        next: () => {
          this.evidencePanel?.clearBusy();
          this.evidenceMutated.emit();
        },
        error: () => this.evidencePanel?.clearBusy(),
      });
  }

  renderMarkdown(md: string): string {
    const jobId = this.jobId();
    const watchPath = this.watchPath();
    return markdownToHtml(md ?? '', {
      resolveImageSrc: (src) => resolveProtocolImageSrc(src, jobId, watchPath),
    });
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString();
  }
}

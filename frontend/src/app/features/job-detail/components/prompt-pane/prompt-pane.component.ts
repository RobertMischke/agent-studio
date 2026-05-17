import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MarkdownRichEditorComponent } from '../../../../components/markdown-rich-editor';
import { JobPromptHistoryEntry, JobTitleHistoryEntry, ReviewEvidenceEntry, ReviewEvidenceSource } from '../../../../models/job.model';
import type { JobScreenshotEntry } from '../../services/screenshots-poll.service';
import { ScreenshotStripComponent } from '../../../screenshots/components/screenshot-strip/screenshot-strip.component';
import { markdownToHtml } from '../../../../components/markdown-utils';
import { MarkdownImageLightboxDirective } from '../../../../directives/markdown-image-lightbox.directive';
import { resolveProtocolImageSrc } from '../protocol-pane/protocol-image-resolver';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';

import { TooltipDirective } from '../../../../components/tooltip';

/** Display-grouping for the Evidence tab, modeled after the reference layout. */
interface EvidenceSection {
  key: ReviewEvidenceSource;
  label: string;
  /** Optional brand-accent token; falls back to `--studio-accent`. */
  accent: 'var(--accent-2)' | 'var(--accent-3)' | 'var(--accent-4)' | 'var(--studio-accent)' | 'var(--accent-warn)';
  entries: ReviewEvidenceEntry[];
}

/**
 * Prompt pane of the job-detail view. Per the reference layout
 * (.reference-layout/detail.jsx) the left detail pane carries a small
 * Description / Evidence tab strip — Description renders prompt.md (via
 * the shared markdown rich editor), Evidence renders the same
 * review-evidence entries the protocol pane consumes, grouped by
 * source (Code Review / Security / Task Checks / Human Notes / Other)
 * with status pills per section. This matches the user's direction
 * that Code Review belongs in the Evidence tab rather than in the
 * Protocol pane.
 */
@Component({
  selector: 'app-prompt-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownRichEditorComponent, MarkdownImageLightboxDirective, TooltipDirective, StudioIconComponent],
  templateUrl: './prompt-pane.component.html',
  styleUrls: ['./prompt-pane.component.scss']
})
export class PromptPaneComponent {
  readonly markdown = input<string>('');
  readonly history = input<JobPromptHistoryEntry[]>([]);
  readonly titleHistory = input<JobTitleHistoryEntry[]>([]);
  readonly reviewEvidence = input<ReviewEvidenceEntry[]>([]);
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();
  readonly save = output<string>();

  /** description | evidence. Persisted across sessions in localStorage. */
  readonly activeTab = signal<'description' | 'evidence'>(
    (typeof window !== 'undefined' && window.localStorage?.getItem('atp.detail.left-tab') === 'evidence')
      ? 'evidence' : 'description',
  );

  setTab(tab: 'description' | 'evidence'): void {
    this.activeTab.set(tab);
    try { window.localStorage?.setItem('atp.detail.left-tab', tab); } catch { /* ignore */ }
  }

  /** Total evidence count for the tab badge. */
  readonly evidenceCount = computed(() => this.reviewEvidence().length);

  /** Evidence entries grouped into the reference's Evidence-tab sections. */
  readonly evidenceSections = computed<EvidenceSection[]>(() => {
    const entries = this.reviewEvidence();
    const sections: EvidenceSection[] = [
      { key: 'code-review',    label: 'Code Review',  accent: 'var(--accent-3)', entries: [] },
      { key: 'security-audit', label: 'Security',     accent: 'var(--accent-warn)', entries: [] },
      { key: 'task-check',     label: 'Task Checks',  accent: 'var(--accent-2)', entries: [] },
      { key: 'human-note',     label: 'Human Notes',  accent: 'var(--accent-4)', entries: [] },
      { key: 'other',          label: 'Other',        accent: 'var(--studio-accent)', entries: [] },
    ];
    const byKey = new Map(sections.map(s => [s.key, s]));
    for (const e of entries) byKey.get(e.source)?.entries.push(e);
    return sections.filter(s => s.entries.length > 0);
  });

  /** Maps severity → border-left class. */
  severityClass(sev: ReviewEvidenceEntry['severity']): string {
    return sev === 'high' ? 'pass-fail' : sev === 'warn' ? 'pass-defer' : 'pass-info';
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

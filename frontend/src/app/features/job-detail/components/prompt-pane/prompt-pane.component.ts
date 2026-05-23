import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MarkdownRichEditorComponent } from '../../../../components/markdown-rich-editor/markdown-rich-editor';
import { MarkdownViewComponent } from '../../../../components/markdown-view/markdown-view.component';
import { JobInfo, JobPromptHistoryEntry, JobTitleHistoryEntry, ReviewEvidenceEntry, ReviewEvidenceSource } from '../../../../models/job.model';
import type { JobScreenshot } from '../../../screenshots';
import { ScreenshotStripComponent } from '../../../screenshots/components/screenshot-strip/screenshot-strip.component';
import { ReviewEvidencePanelComponent } from '../protocol-pane/review-evidence-panel/review-evidence-panel.component';
import { CodeReviewPanelComponent } from '../protocol-pane/code-review-panel/code-review-panel.component';
import { resolveProtocolImageSrc } from '../protocol-pane/protocol-image-resolver';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { PaneHeaderComponent } from '../../../../components/pane-header/pane-header.component';

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
  imports: [MarkdownRichEditorComponent, MarkdownViewComponent, StudioIconComponent, PaneHeaderComponent, ScreenshotStripComponent, ReviewEvidencePanelComponent, CodeReviewPanelComponent],
  templateUrl: './prompt-pane.component.html',
  styleUrls: ['./prompt-pane.component.scss']
})
export class PromptPaneComponent {
  readonly markdown = input<string>('');
  readonly history = input<JobPromptHistoryEntry[]>([]);
  readonly titleHistory = input<JobTitleHistoryEntry[]>([]);
  readonly reviewEvidence = input<ReviewEvidenceEntry[]>([]);
  readonly screenshots = input<JobScreenshot[]>([]);
  readonly job = input<JobInfo | null>(null);
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();
  readonly save = output<string>();
  readonly evidenceAcknowledge = output<{ entry: ReviewEvidenceEntry; acknowledged: boolean }>();
  readonly evidenceCreateFollowup = output<ReviewEvidenceEntry>();

  /** description | evidence | code-review. Persisted across sessions in localStorage. */
  readonly activeTab = signal<'description' | 'evidence' | 'code-review'>(
    (() => {
      if (typeof window === 'undefined') return 'description';
      const v = window.localStorage?.getItem('atp.detail.left-tab');
      if (v === 'evidence' || v === 'code-review') return v;
      return 'description';
    })(),
  );

  setTab(tab: 'description' | 'evidence' | 'code-review'): void {
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

  /** Resolver factory for prompt-history image refs (`attachments/foo.png` ->
   *  job-folder API URL). Stable identity per render so `<app-markdown>`'s
   *  signal doesn't churn unnecessarily. */
  readonly imageResolver = computed<(src: string) => string>(() => {
    const jobId = this.jobId();
    const watchPath = this.watchPath();
    return (src: string) => resolveProtocolImageSrc(src, jobId, watchPath);
  });

  formatTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString();
  }

  /** "created Xh ago" / "Xm ago" / "Xd ago" relative-time rendering for
   *  the description meta strip. */
  formatRelativeTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const diffMs = Date.now() - d.getTime();
    const minutes = Math.round(diffMs / 60_000);
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.round(hours / 24);
    if (days < 30) return `${days}d ago`;
    const months = Math.round(days / 30);
    if (months < 12) return `${months}mo ago`;
    return `${Math.round(months / 12)}y ago`;
  }

  /** Human label for a kanban-state slug. Slugs match
   *  STATE_TO_LANE in job.service.ts. */
  laneLabel(state: string): string {
    switch (state) {
      case '0-backlog':            return 'Backlog';
      case '1-preparation':        return 'In Preparation';
      case '1a-orchestrator-prep': return 'Orchestrator Prep';
      case '1b-needs-human-review': return 'Needs Human Review';
      case '2-ready':              return 'Human Ready';
      case '3-progress':           return 'In Progress';
      case '3a-failed-pickup':     return 'Failed Pickup';
      case '4-auto-review':        return 'Auto Review';
      case '5-human-review':       return 'Human Review';
      case '6-completed':          return 'Completed';
      case '7-archive':            return 'Archive';
      default:                     return state ?? '';
    }
  }
}

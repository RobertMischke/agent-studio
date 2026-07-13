import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { WikiNodeType, WikiPulse, WikiPulseDriftArea, WikiPulseFeedItem, WorkbenchListItem } from '../../../../../models/project-docs.model';
import { WorkbenchInboxComponent } from './workbench-inbox/workbench-inbox.component';

/** What the parent needs to open a page from a Pulse row. */
export interface WikiPulseOpenRequest {
  relPath: string;
  type: WikiNodeType;
}

/** A change-feed day bucket (newest day first). */
interface WikiPulseFeedGroup {
  key: string;
  label: string;
  items: WikiPulseFeedItem[];
}

type WikiPulseTone = 'good' | 'info' | 'warn' | 'bad' | 'muted';

/**
 * The generated wiki Pulse landing view (PULSE-1): the read-only entry surface
 * the wiki opens on. It is not a wiki page - it is composed from git history and
 * the docs tree, never editable. Three sections answer "what changed, what needs
 * sorting, and how stale is the knowledge":
 *
 *  - the drift grade bar (per Workstream frame area, deterministic, no LLM),
 *  - the change feed grouped by day (frame-area badge + task key, click to open),
 *  - the inbox of loose / unfiled pages (an empty inbox is the healthy state).
 *
 * Purely presentational: the parent owns fetching and feeds the fully-composed
 * {@link WikiPulse}; this component only formats and emits navigation intent.
 */
@Component({
  selector: 'app-wiki-pulse',
  standalone: true,
  imports: [StudioIconComponent, TooltipDirective, WorkbenchInboxComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-pulse.component.html',
  styleUrl: './wiki-pulse.component.scss',
})
export class WikiPulseComponent {
  readonly pulse = input<WikiPulse | null>(null);
  readonly loading = input(false);

  readonly openPage = output<WikiPulseOpenRequest>();
  readonly openWorkbench = output<WorkbenchListItem>();

  readonly feed = computed(() => this.pulse()?.feed ?? null);
  readonly inbox = computed(() => this.pulse()?.inbox ?? null);
  readonly drift = computed(() => this.pulse()?.drift ?? null);
  readonly warnings = computed(() => this.pulse()?.warnings ?? null);
  readonly activity = computed(() => this.pulse()?.activity ?? null);

  /** Change-feed rows bucketed by local calendar day, newest day first. */
  readonly feedGroups = computed<WikiPulseFeedGroup[]>(() => {
    const items = this.feed()?.items ?? [];
    const groups: WikiPulseFeedGroup[] = [];
    const index = new Map<string, number>();
    for (const item of items) {
      const key = this.dayKey(item.authorDateUtc);
      let gi = index.get(key);
      if (gi === undefined) {
        gi = groups.length;
        index.set(key, gi);
        groups.push({ key, label: this.dayLabel(item.authorDateUtc), items: [] });
      }
      groups[gi].items.push(item);
    }
    return groups;
  });

  readonly hasFeedItems = computed(() => (this.feed()?.items.length ?? 0) > 0);

  /** True when the inbox is available and clear - the healthy resting state. */
  readonly inboxClear = computed(() => {
    const inbox = this.inbox();
    return !!inbox && inbox.available && inbox.count === 0;
  });

  openFeed(item: WikiPulseFeedItem): void {
    this.openPage.emit({ relPath: item.relPath, type: this.typeForRel(item.relPath) });
  }

  /** Tone for a drift grade (Fresh -> good, Aging -> warn, Stale -> bad). */
  gradeTone(grade: string | null | undefined): WikiPulseTone {
    switch ((grade ?? '').toLowerCase()) {
      case 'fresh': return 'good';
      case 'aging': return 'warn';
      case 'stale': return 'bad';
      default: return 'muted';
    }
  }

  /** Short area caption for the compact grade-bar segment. */
  areaCaption(count: number, grade: string): string {
    if (grade === 'Empty') return 'no pages';
    return count === 1 ? '1 commit' : `${count} commits`;
  }

  /** Full drift breakdown for a grade-bar segment's hover tooltip. */
  areaTooltip(area: WikiPulseDriftArea): string {
    return `${area.title} - ${area.grade} · ${area.gradedPageCount} graded / ${area.pageCount} pages · worst ${area.worstCommitCount} commits`;
  }

  typeForRel(relPath: string): WikiNodeType {
    const ext = relPath.toLowerCase().split('.').pop() ?? '';
    if (ext === 'html' || ext === 'htm') return 'html';
    if (ext === 'json') return 'json';
    return 'md';
  }

  kindLabel(type: WikiNodeType): string {
    return type === 'html' ? 'HTML' : type === 'json' ? 'JSON' : 'MD';
  }

  /** Compact relative time ("3h ago"), falling back to a locale date. */
  relativeTime(iso: string): string {
    const then = new Date(iso);
    const ms = then.getTime();
    if (Number.isNaN(ms)) return iso;
    const diff = Date.now() - ms;
    if (diff < 0) return then.toLocaleDateString();
    const min = Math.floor(diff / 60000);
    if (min < 1) return 'just now';
    if (min < 60) return `${min}m ago`;
    const hours = Math.floor(min / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 30) return `${days}d ago`;
    return then.toLocaleDateString();
  }

  absoluteTime(iso: string): string {
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
  }

  runtime(iso: string): string {
    const ms = Date.now() - new Date(iso).getTime();
    if (!Number.isFinite(ms) || ms < 0) return 'just started';
    const minutes = Math.max(1, Math.floor(ms / 60_000));
    if (minutes < 60) return `${minutes}m`;
    const hours = Math.floor(minutes / 60);
    return `${hours}h ${minutes % 60}m`;
  }

  private dayKey(iso: string): string {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
  }

  /** Human day heading: Today / Yesterday / a locale date. */
  private dayLabel(iso: string): string {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const now = new Date();
    const startOf = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
    const dayMs = 86_400_000;
    const delta = Math.round((startOf(now) - startOf(d)) / dayMs);
    if (delta === 0) return 'Today';
    if (delta === 1) return 'Yesterday';
    return d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
  }
}

import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from '@coding-agent/chat/shared';
import { WikiNodeType } from '../../../../../models/project-docs.model';

/** A recently-edited page row (page / git author / when) for the dashboard. */
export interface WikiDashboardRecentRow {
  relPath: string;
  title: string;
  author: string;
  authorDateUtc: string;
  type: WikiNodeType;
}

/** A high-drift page row surfaced for attention on the dashboard. */
export interface WikiDashboardDriftRow {
  relPath: string;
  title: string;
  type: WikiNodeType;
  grade: string | null;
  score: number | null;
  tone: 'warn' | 'bad';
  summary: string | null;
}

/** What the parent needs to open a page from a dashboard quick-action. */
export interface WikiDashboardOpenRequest {
  relPath: string;
  type: WikiNodeType;
}

/**
 * Wiki landing dashboard: an at-a-glance entry surface that answers "what
 * changed last, and what needs attention". Two panels - recently edited pages
 * (page / author / when, from git history) and high-drift pages (worst first) -
 * each row a quick jump straight into the page or its drift report.
 *
 * Purely presentational: the parent owns fetching and tree derivation and feeds
 * fully-formed rows in; the dashboard only formats and emits navigation intent.
 */
@Component({
  selector: 'app-wiki-dashboard',
  standalone: true,
  imports: [StudioIconComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-dashboard.component.html',
  styleUrl: './wiki-dashboard.component.scss',
})
export class WikiDashboardComponent {
  readonly recent = input<WikiDashboardRecentRow[]>([]);
  readonly highDrift = input<WikiDashboardDriftRow[]>([]);
  readonly loading = input(false);

  /** Open the page in the reader. */
  readonly openPage = output<WikiDashboardOpenRequest>();
  /** Open the page on its drift report tab. */
  readonly openDrift = output<WikiDashboardOpenRequest>();

  readonly hasRecent = computed(() => this.recent().length > 0);
  readonly hasDrift = computed(() => this.highDrift().length > 0);

  kindLabel(type: WikiNodeType): string {
    return type === 'html' ? 'HTML' : type === 'json' ? 'JSON' : 'MD';
  }

  /** Compact relative time ("3h ago"), falling back to a locale date string. */
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
}

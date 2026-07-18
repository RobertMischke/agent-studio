import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { WikiSearchResponse, WikiSearchResult } from '../../../../../models/project-docs.model';

/**
 * Wiki search result list for the content pane. Purely presentational: the
 * parent owns the query state, debounce, and HTTP; this component renders the
 * hits (title, dimmed relPath, `<em>`-highlighted snippet, relative time) and
 * emits navigation / semantic-expansion intent.
 *
 * Snippets are bound via `[innerHTML]`; that is safe here only because
 * `ProjectDocsService.searchWiki` sanitises every snippet down to `<em>`-only
 * markup before it reaches this component (see `sanitizeWikiSearchSnippet`),
 * and Angular's default innerHTML sanitisation still applies on top.
 */
@Component({
  selector: 'app-wiki-search-results',
  standalone: true,
  imports: [StudioIconComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-search-results.component.html',
  styleUrl: './wiki-search-results.component.scss',
})
export class WikiSearchResultsComponent {
  readonly query = input.required<string>();
  readonly response = input<WikiSearchResponse | null>(null);
  readonly loading = input(false);
  /** True while the semantic-expansion call is in flight. */
  readonly semanticLoading = input(false);
  /** True once the user asked for semantic expansion (drives the fallback hint). */
  readonly semanticRequested = input(false);
  readonly error = input<string | null>(null);

  readonly openResult = output<WikiSearchResult>();
  readonly expandSemantic = output<void>();

  readonly results = computed(() => this.response()?.results ?? []);
  readonly expandedTerms = computed(() => this.response()?.expandedTerms ?? []);

  /** Semantic expansion was requested but the backend could not provide it. */
  readonly semanticUnavailable = computed(() => {
    const response = this.response();
    return this.semanticRequested()
      && !this.semanticLoading()
      && response !== null
      && response.semanticUsed === false;
  });

  /** Compact relative time ("3h ago"), falling back to a locale date. */
  relativeTime(iso: string | null): string {
    if (!iso) return '';
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

  absoluteTime(iso: string | null): string {
    if (!iso) return '';
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
  }
}

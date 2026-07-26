import { Injectable, OnDestroy, inject } from '@angular/core';
import type { WikiRecentEdits } from '../../../models/project-docs.model';
import { ProjectDocsService } from '../../../services/project-docs.service';
import {
  clearVisibleInterval,
  setVisibleInterval,
  type VisibleIntervalHandle,
} from '../../../utils/visible-interval';

export const WIKI_LIVE_REFRESH_MS = 15_000;

/**
 * Component-scoped transport for Wiki live refresh.
 *
 * Each mounted Wiki surface gets its own instance. Polls therefore stop with
 * the route, skip hidden-document ticks, and never overlap an active request.
 */
@Injectable()
export class WikiLiveRefreshService implements OnDestroy {
  private readonly docs = inject(ProjectDocsService);

  private recentTimer: VisibleIntervalHandle | null = null;
  private recentGeneration = 0;
  private recentInFlight = false;
  private recentEtag: string | null = null;

  private pageTimer: VisibleIntervalHandle | null = null;
  private pageGeneration = 0;
  private pageInFlight = false;
  private pageEtag: string | null = null;
  private pageChanged: ((etag: string) => void) | null = null;

  watchRecentEdits(
    projectName: string,
    limit: number,
    onModified: (edits: WikiRecentEdits) => void,
  ): void {
    this.stopRecentEdits();
    const generation = this.recentGeneration;
    this.recentTimer = setVisibleInterval(() => {
      if (this.recentInFlight) return;
      this.recentInFlight = true;
      this.docs.getWikiRecentEditsVersion(projectName, limit, this.recentEtag).subscribe({
        next: response => {
          if (generation !== this.recentGeneration) return;
          this.recentEtag = response.etag ?? this.recentEtag;
          if (response.modified && response.body) onModified(response.body);
        },
        error: () => this.finishRecentRequest(generation),
        complete: () => this.finishRecentRequest(generation),
      });
    }, WIKI_LIVE_REFRESH_MS);
  }

  watchPage(projectName: string, relPath: string, onChanged: (etag: string) => void): void {
    this.stopPage();
    const generation = this.pageGeneration;
    this.pageChanged = onChanged;
    this.pageTimer = setVisibleInterval(() => {
      if (this.pageInFlight) return;
      this.pageInFlight = true;
      const requestedEtag = this.pageEtag;
      this.docs.getWikiFileHistoryVersion(projectName, relPath, requestedEtag).subscribe({
        next: response => {
          if (generation !== this.pageGeneration) return;
          const nextEtag = response.etag;
          this.pageEtag = nextEtag ?? this.pageEtag;
          if (response.modified && requestedEtag && nextEtag && requestedEtag !== nextEtag) {
            this.pageChanged?.(nextEtag);
          }
        },
        error: () => this.finishPageRequest(generation),
        complete: () => this.finishPageRequest(generation),
      });
    }, WIKI_LIVE_REFRESH_MS);
  }

  setPageVersion(etag: string | null): void {
    this.pageEtag = etag;
  }

  stopRecentEdits(): void {
    clearVisibleInterval(this.recentTimer);
    this.recentTimer = null;
    this.recentGeneration++;
    this.recentInFlight = false;
    this.recentEtag = null;
  }

  stopPage(): void {
    clearVisibleInterval(this.pageTimer);
    this.pageTimer = null;
    this.pageGeneration++;
    this.pageInFlight = false;
    this.pageEtag = null;
    this.pageChanged = null;
  }

  ngOnDestroy(): void {
    this.stopRecentEdits();
    this.stopPage();
  }

  private finishRecentRequest(generation: number): void {
    if (generation === this.recentGeneration) this.recentInFlight = false;
  }

  private finishPageRequest(generation: number): void {
    if (generation === this.pageGeneration) this.pageInFlight = false;
  }
}

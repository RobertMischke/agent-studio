import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  AcceptCouncilNoteResponse,
  DesignActionKind,
  DesignActionQueueResponse,
  DesignCouncilResponse,
  DesignOverviewResponse,
  DesignReferencesResponse,
} from '../features/project-detail/components/uxui-panel/uxui-panel.types';

/**
 * Read + manual-trigger surface for the project UX/UI panel (slice 6 of
 * the quality-system mockup, docs/mockups/quality-system/). Wraps the
 * <c>/api/projects/&lt;name&gt;/design/...</c> endpoints. The service
 * stays thin so the panel component owns its loading state.
 */
@Injectable({ providedIn: 'root' })
export class DesignService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/projects';

  getOverview(projectName: string): Observable<DesignOverviewResponse> {
    return this.http.get<DesignOverviewResponse>(
      `${this.baseUrl}/${encodeURIComponent(projectName)}/design/overview`,
    );
  }

  listReferences(projectName: string): Observable<DesignReferencesResponse> {
    return this.http.get<DesignReferencesResponse>(
      `${this.baseUrl}/${encodeURIComponent(projectName)}/design/references`,
    );
  }

  listCouncilNotes(projectName: string): Observable<DesignCouncilResponse> {
    return this.http.get<DesignCouncilResponse>(
      `${this.baseUrl}/${encodeURIComponent(projectName)}/design/council`,
    );
  }

  readCouncilNote(projectName: string, fileName: string): Observable<{ fileName: string; content: string }> {
    return this.http.get<{ fileName: string; content: string }>(
      `${this.baseUrl}/${encodeURIComponent(projectName)}/design/council/${encodeURIComponent(fileName)}`,
    );
  }

  /**
   * Queue a design-loop CLI job. Backend returns 409 with
   * <c>error: "design-action-already-pending"</c> when one is already in
   * an open lane on this project; the panel surfaces that as an inline
   * error chip.
   */
  runAction(projectName: string, action: DesignActionKind): Observable<DesignActionQueueResponse> {
    return this.http.post<DesignActionQueueResponse>(
      `${this.baseUrl}/${encodeURIComponent(projectName)}/design/actions/${action}`,
      {},
    );
  }

  acceptCouncilNote(projectName: string, fileName: string): Observable<AcceptCouncilNoteResponse> {
    return this.http.post<AcceptCouncilNoteResponse>(
      `${this.baseUrl}/${encodeURIComponent(projectName)}/design/council/${encodeURIComponent(fileName)}/accept`,
      {},
    );
  }
}

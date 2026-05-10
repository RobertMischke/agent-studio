import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import type {
  SecurityAuditQueueResponse,
  SecurityBaselineResponse,
  SecurityReviewListResponse,
} from '../features/project-detail';

/**
 * Read + manual-trigger surface for the project Security panel (slice 1
 * of the quality-system mockup, docs/mockups/quality-system/). Wraps the
 * <c>/api/projects/&lt;name&gt;/security/...</c> endpoints. The service
 * stays thin: no caching, no signals - the panel component owns its
 * loading state so reload semantics stay predictable when the project
 * changes.
 */
@Injectable({ providedIn: 'root' })
export class SecurityService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/projects';

  listReviews(projectName: string): Observable<SecurityReviewListResponse> {
    return this.http.get<SecurityReviewListResponse>(
      `${this.baseUrl}/${encodeURIComponent(projectName)}/security/reviews`,
    );
  }

  getBaseline(projectName: string): Observable<SecurityBaselineResponse> {
    return this.http.get<SecurityBaselineResponse>(
      `${this.baseUrl}/${encodeURIComponent(projectName)}/security/baseline`,
    );
  }

  readReview(projectName: string, fileName: string): Observable<{ fileName: string; content: string }> {
    return this.http.get<{ fileName: string; content: string }>(
      `${this.baseUrl}/${encodeURIComponent(projectName)}/security/reviews/${encodeURIComponent(fileName)}`,
    );
  }

  /**
   * Queue a new security audit job. Backend returns 409 with
   * <c>error: "audit-already-pending"</c> when one is already running or
   * waiting; the panel surfaces that as an inline error chip instead of
   * silently retrying.
   */
  queueAudit(projectName: string): Observable<SecurityAuditQueueResponse> {
    return this.http.post<SecurityAuditQueueResponse>(
      `${this.baseUrl}/${encodeURIComponent(projectName)}/security/audit`,
      {},
    );
  }
}

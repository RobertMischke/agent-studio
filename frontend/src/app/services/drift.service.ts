import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  DriftArchitectureSurfaceResponse,
  DriftFindingStatus,
  DriftReportDetailResponse,
  DriftReportListResponse,
  ElementStateOverride,
} from '../models/drift.model';

/**
 * Read + element-state-mutation surface for the project Drift view. Backed by
 * the in-memory `DriftReportStore` and the `ArchitectureElementStateStore`;
 * safe to poll. Element-status writes are persisted immediately and
 * reflected on the next list/get.
 */
@Injectable({ providedIn: 'root' })
export class DriftService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/drift';

  /**
   * Get the latest architecture map for a project. Returns `model=null`
   * when no drift report carries an `architectureModel`. Element-state
   * overrides are returned in the same payload so the marble surface can
   * apply them without a second roundtrip.
   */
  getArchitecture(project: string): Observable<DriftArchitectureSurfaceResponse> {
    return this.http.get<DriftArchitectureSurfaceResponse>(
      `${this.baseUrl}/${encodeURIComponent(project)}/architecture`,
    );
  }

  /**
   * List recent drift reports for a project, newest-first. The backend caps
   * `limit` at 500; the surface typically requests 50-100. `refresh=true`
   * forces the in-memory projection to re-read the on-disk index, which is
   * useful right after a planted fixture or a manual append.
   */
  listReports(project: string, opts?: {
    limit?: number;
    trigger?: string;
    scoreBand?: string;
    refresh?: boolean;
  }): Observable<DriftReportListResponse> {
    const params: string[] = [];
    if (opts?.limit) params.push(`limit=${opts.limit}`);
    if (opts?.trigger) params.push(`trigger=${encodeURIComponent(opts.trigger)}`);
    if (opts?.scoreBand) params.push(`scoreBand=${encodeURIComponent(opts.scoreBand)}`);
    // Always force the in-memory projection to re-read from disk on read.
    // Drift evidence is small per-project (one Markdown + one JSON line per
    // report) and rotates on user action; the cost of a re-read is well
    // under a millisecond. Without refresh=true the projection can hold
    // stale records across test invocations and the surface renders pre-
    // wipe data while the disk has already moved on. The opt-in
    // `refresh=false` escape is left for any future low-volume reader.
    if (opts?.refresh !== false) params.push('refresh=true');
    // Cache-buster: prevent any intermediate HTTP cache from serving a
    // stale projection response.
    params.push(`_=${Date.now()}`);
    const qs = `?${params.join('&')}`;
    return this.http.get<DriftReportListResponse>(
      `${this.baseUrl}/${encodeURIComponent(project)}/reports${qs}`,
      { headers: { 'Cache-Control': 'no-cache' } },
    );
  }

  /**
   * Get one drift report plus its Markdown sibling. The Markdown body is the
   * durable human artifact; the typed record is the additive convenience and
   * may be Unstructured or MalformedJson.
   */
  getReport(project: string, reportId: string): Observable<DriftReportDetailResponse> {
    return this.http.get<DriftReportDetailResponse>(
      `${this.baseUrl}/${encodeURIComponent(project)}/reports/${encodeURIComponent(reportId)}`,
    );
  }

  /**
   * Run the ADR / Code Drift action. Without an `agentResponse` body the
   * backend produces an Unstructured "evidence + prompt" report.
   */
  runAdrCodeDrift(project: string, agentResponse?: string | null): Observable<DriftReportDetailResponse> {
    return this.http.post<DriftReportDetailResponse>(
      `${this.baseUrl}/${encodeURIComponent(project)}/actions/adr-code-drift`,
      { agentResponse: agentResponse ?? null },
    );
  }

  /**
   * Run the Docs / Marketing Drift action. Same envelope as ADR / Code:
   * empty body = evidence-only report.
   */
  runDocsMarketingDrift(project: string, agentResponse?: string | null): Observable<DriftReportDetailResponse> {
    return this.http.post<DriftReportDetailResponse>(
      `${this.baseUrl}/${encodeURIComponent(project)}/actions/docs-marketing-drift`,
      { agentResponse: agentResponse ?? null },
    );
  }

  setElementStatus(
    project: string,
    modelId: string,
    elementId: string,
    status: DriftFindingStatus,
    note?: string | null,
  ): Observable<ElementStateOverride> {
    return this.http.post<ElementStateOverride>(
      `${this.baseUrl}/${encodeURIComponent(project)}` +
        `/architecture/${encodeURIComponent(modelId)}` +
        `/elements/${encodeURIComponent(elementId)}/status`,
      { status, note: note ?? null },
    );
  }
}

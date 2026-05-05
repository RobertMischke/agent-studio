import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  AnalysisReportDetailResponse,
  AnalysisReportListResponse,
} from '../models/analysis-report.model';

/**
 * Read + manual-trigger surface for the Analysis Reports project view.
 * Backed by the in-memory <c>AnalysisReportStore</c> projection; safe to poll.
 */
@Injectable({ providedIn: 'root' })
export class AnalysisReportService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/analysis';

  list(project: string, opts?: { trigger?: string; severity?: string; topic?: string; limit?: number }):
    Observable<AnalysisReportListResponse> {
    const params: string[] = [];
    if (opts?.trigger) params.push(`trigger=${encodeURIComponent(opts.trigger)}`);
    if (opts?.severity) params.push(`severity=${encodeURIComponent(opts.severity)}`);
    if (opts?.topic) params.push(`topic=${encodeURIComponent(opts.topic)}`);
    if (opts?.limit) params.push(`limit=${opts.limit}`);
    const qs = params.length ? `?${params.join('&')}` : '';
    return this.http.get<AnalysisReportListResponse>(
      `${this.baseUrl}/${encodeURIComponent(project)}/reports${qs}`,
    );
  }

  get(project: string, reportId: string): Observable<AnalysisReportDetailResponse> {
    return this.http.get<AnalysisReportDetailResponse>(
      `${this.baseUrl}/${encodeURIComponent(project)}/reports/${encodeURIComponent(reportId)}`,
    );
  }

  trigger(project: string, topic: string, summary?: string): Observable<AnalysisReportDetailResponse> {
    return this.http.post<AnalysisReportDetailResponse>(
      `${this.baseUrl}/${encodeURIComponent(project)}/reports`,
      { topic, summary: summary ?? null },
    );
  }

  getSchedule(project: string): Observable<Record<string, string>> {
    return this.http.get<Record<string, string>>(
      `${this.baseUrl}/${encodeURIComponent(project)}/schedule`,
    );
  }

  setSchedule(project: string, topic: string, cadence: string): Observable<Record<string, string>> {
    return this.http.put<Record<string, string>>(
      `${this.baseUrl}/${encodeURIComponent(project)}/schedule`,
      { topic, cadence },
    );
  }
}

import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  DriftArchitectureSurfaceResponse,
  DriftFindingStatus,
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

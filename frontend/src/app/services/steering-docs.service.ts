import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { SteeringDocsOverview, SteeringFileContent } from '../models/steering-docs.model';

/**
 * Project-level Steering Docs read API. Fetches the inventory of
 * agent-facing instruction sources and reads one file at a time.
 *
 * The corresponding backend service is read-only: edits and proposed
 * documentation updates land as queued tasks via the existing
 * job-creation endpoint, not through this service.
 */
@Injectable({ providedIn: 'root' })
export class SteeringDocsService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api';

  getOverview(projectName: string) {
    return this.http.get<SteeringDocsOverview>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/steering`
    );
  }

  getFile(projectName: string, relPath: string) {
    return this.http.get<SteeringFileContent>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/steering/files/${this.encodeRelPath(relPath)}`
    );
  }

  private encodeRelPath(relPath: string): string {
    return relPath.split('/').map(encodeURIComponent).join('/');
  }
}

import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';

/** Git identity read at the Preview command's effective working directory. */
export interface ProjectUrlRepositoryContext {
  projectName: string;
  repositoryName: string | null;
  workingDirectory: string | null;
  repoRoot: string | null;
  isRepo: boolean;
  branch: string | null;
  headSha: string | null;
  headShortSha: string | null;
  comparisonRef: string | null;
  comparisonKind: 'upstream' | 'integration' | null;
  ahead: number;
  behind: number;
  isDirty: boolean;
  error: string | null;
}

@Injectable({ providedIn: 'root' })
export class ProjectUrlContextService {
  private readonly http = inject(HttpClient);

  load(projectId: string, urlId: string) {
    return this.http.get<ProjectUrlRepositoryContext>(
      `/api/projects/${encodeURIComponent(projectId)}/urls/${encodeURIComponent(urlId)}/context`,
    );
  }
}

import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import type { Observable } from 'rxjs';
import type {
  CleanupExecutionItem,
  GitCleanupPlan,
  GitCleanupResult,
  GitFileChange,
  GitProjectInventory,
  ProjectIntegrationView,
} from '../features/git';

/** Diff payload envelope shared with the per-task commit-diff endpoints. */
export interface ProjectCommitDiff {
  diff: string;
  hasDiff: boolean;
  emptyReason: string | null;
}

/**
 * HTTP wrapper for the Project Hub Git View endpoints. Thin and stateless:
 * the panel owns its own signals and calls these for the branch/worktree
 * inventory and for a browsed commit's files + diff. All three endpoints are
 * read-only and cached server-side (~3 s for the inventory).
 */
@Injectable({ providedIn: 'root' })
export class ProjectGitService {
  private readonly http = inject(HttpClient);

  /** Branch / worktree / recent-history inventory for one project. */
  getInventory(project: string): Observable<GitProjectInventory> {
    return this.http.get<GitProjectInventory>('/api/git/inventory', {
      params: new HttpParams().set('project', project),
    });
  }

  /** Honest remote integration queue and develop-to-main promotion delta. */
  getIntegration(project: string): Observable<ProjectIntegrationView> {
    return this.http.get<ProjectIntegrationView>('/api/git/integration', {
      params: new HttpParams().set('project', project),
    });
  }

  /** Changed-file list for a commit browsed in the Git View. */
  getCommitFiles(project: string, sha: string): Observable<{ sha: string; files: GitFileChange[] }> {
    return this.http.get<{ sha: string; files: GitFileChange[] }>('/api/git/project-commit/files', {
      params: new HttpParams().set('project', project).set('sha', sha),
    });
  }

  /** Unified diff for a browsed commit, optionally scoped to one path. */
  getCommitDiff(project: string, sha: string, path?: string | null): Observable<ProjectCommitDiff> {
    let params = new HttpParams().set('project', project).set('sha', sha);
    if (path) params = params.set('path', path);
    return this.http.get<ProjectCommitDiff>('/api/git/project-commit/diff', { params });
  }

  /**
   * Read-only cleanup dry-run: which merged task/* branches, refs/backups/* refs
   * and stale worktrees can be pruned against the integration branch. Nothing is
   * deleted by this call.
   */
  getCleanupPlan(project: string): Observable<GitCleanupPlan> {
    return this.http.get<GitCleanupPlan>('/api/git/cleanup/plan', {
      params: new HttpParams().set('project', project),
    });
  }

  /**
   * Executes an operator-confirmed subset of the cleanup plan. The backend
   * re-verifies each item is still merged before deleting (AGT-1945), so a stale
   * selection can never drop unmerged work; the result reports n-deleted / m-kept.
   */
  executeCleanup(project: string, items: CleanupExecutionItem[]): Observable<GitCleanupResult> {
    return this.http.post<GitCleanupResult>(
      '/api/git/cleanup/execute',
      { items },
      { params: new HttpParams().set('project', project) },
    );
  }
}

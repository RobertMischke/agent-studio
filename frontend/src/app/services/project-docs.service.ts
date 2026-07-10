import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import {
  ArchitectureOverview,
  SecurityFileContent,
  SecurityMeta,
  SecurityOverview,
  WikiFileContent,
  WikiFileHistory,
  WikiFileSaveResult,
  WikiGradingAbortResponse,
  WikiGradingRunBody,
  WikiGradingRunStatus,
  WikiGradingStatusResponse,
  WikiMaintenanceModelConfig,
  WikiOverview,
  WikiPulse,
  WikiRecentEdits,
  WikiRevisionContent,
  WikiTree
} from '../models/project-docs.model';

/**
 * Read/write surface for the project-level Security archive and the
 * Architecture decisions browser. Prototype only — no caching, the
 * components poll on open.
 */
@Injectable({ providedIn: 'root' })
export class ProjectDocsService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api';

  getSecurityOverview(projectName: string) {
    return this.http.get<SecurityOverview>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/security`
    );
  }

  getSecurityFile(projectName: string, relPath: string) {
    return this.http.get<SecurityFileContent>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/security/files/${this.encodeRelPath(relPath)}`
    );
  }

  putSecurityFile(projectName: string, relPath: string, content: string) {
    return this.http.put<{ relPath: string; saved: boolean }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/security/files/${this.encodeRelPath(relPath)}`,
      { content }
    );
  }

  putSecurityMeta(projectName: string, meta: SecurityMeta) {
    return this.http.put<{ saved: boolean }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/security/meta`,
      meta
    );
  }

  getWikiOverview(projectName: string) {
    return this.http.get<WikiOverview>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki`
    );
  }

  /** The physical docs/ folder tree (folders + .md/.html files), the wiki nav source. */
  getWikiTree(projectName: string) {
    return this.http.get<WikiTree>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/tree`
    );
  }

  /** Recently-edited wiki pages (page / git author / when), newest first. */
  getWikiRecentEdits(projectName: string, limit = 12) {
    return this.http.get<WikiRecentEdits>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/recent?limit=${limit}`
    );
  }

  /**
   * The generated wiki Pulse landing view (PULSE-1): change feed + inbox +
   * drift grade bar, composed server-side in one call so the landing surface
   * does not multiply the per-doc git lookups.
   */
  getWikiPulse(projectName: string, feedLimit = 12) {
    return this.http.get<WikiPulse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/pulse?feedLimit=${feedLimit}`
    );
  }

  getWikiFile(projectName: string, relPath: string) {
    return this.http.get<WikiFileContent>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/files/${this.encodeRelPath(relPath)}`
    );
  }

  putWikiFile(projectName: string, relPath: string, content: string) {
    return this.http.put<WikiFileSaveResult>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/files/${this.encodeRelPath(relPath)}`,
      { content }
    );
  }

  /** Absolute API URL for a wiki image/diagram asset (used by the image resolver). */
  wikiAssetUrl(projectName: string, relPath: string): string {
    return `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/assets/${this.encodeRelPath(relPath)}`;
  }

  /** Per-document provenance (model/when/why) + git history, newest first. */
  getWikiFileHistory(projectName: string, relPath: string) {
    return this.http.get<WikiFileHistory>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/history/${this.encodeRelPath(relPath)}`
    );
  }

  /** Content of a wiki doc as it existed at an earlier commit (old-revision view). */
  getWikiRevision(projectName: string, sha: string, relPath: string) {
    return this.http.get<WikiRevisionContent>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/revisions/${encodeURIComponent(sha)}/${this.encodeRelPath(relPath)}`
    );
  }

  /** Create a new wiki page (.md/.html); the server commits it into the repo. */
  createWikiPage(projectName: string, relPath: string, content?: string) {
    return this.http.post<{ relPath: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/pages`,
      { relPath, content: content ?? null }
    );
  }

  /** Create a new wiki folder; the server seeds a .gitkeep and commits it. */
  createWikiFolder(projectName: string, relPath: string) {
    return this.http.post<{ relPath: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/folders`,
      { relPath }
    );
  }

  /** Move/rename a wiki node (file or folder) via git mv + commit. */
  moveWikiNode(projectName: string, fromRelPath: string, toRelPath: string) {
    return this.http.post<{ from: string; to: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/move`,
      { fromRelPath, toRelPath }
    );
  }

  /** Delete a wiki node (file or folder) via git rm + commit. */
  deleteWikiNode(projectName: string, relPath: string) {
    return this.http.delete<{ relPath: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/files/${this.encodeRelPath(relPath)}`
    );
  }

  // ---- Wiki grading maintenance run (AGT-2051) ----

  /** Start a global grading pass. Fields default from the maintenance model. */
  startWikiGrading(projectName: string, body: WikiGradingRunBody = {}) {
    return this.http.post<WikiGradingRunStatus>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/grading/run`,
      body
    );
  }

  /** Poll the latest run status (`status` is null until the first run starts). */
  getWikiGradingStatus(projectName: string) {
    return this.http.get<WikiGradingStatusResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/grading/status`
    );
  }

  /** Request cancellation of an in-flight grading run. */
  abortWikiGrading(projectName: string) {
    return this.http.post<WikiGradingAbortResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/grading/abort`,
      {}
    );
  }

  /** Read the workspace maintenance-model default (CLI-management area). */
  getMaintenanceModel() {
    return this.http.get<WikiMaintenanceModelConfig>(`${this.baseUrl}/cli/maintenance-model`);
  }

  /** Write the workspace maintenance-model default. */
  setMaintenanceModel(config: Partial<WikiMaintenanceModelConfig>) {
    return this.http.put<WikiMaintenanceModelConfig>(`${this.baseUrl}/cli/maintenance-model`, config);
  }

  getArchitectureOverview(projectName: string) {
    return this.http.get<ArchitectureOverview>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/architecture`
    );
  }

  private encodeRelPath(relPath: string): string {
    return relPath.split('/').map(encodeURIComponent).join('/');
  }
}

import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import {
  ArchitectureOverview,
  SecurityFileContent,
  SecurityMeta,
  SecurityOverview,
  WikiFileContent,
  WikiFileHistory,
  WikiOrganization,
  WikiOverview
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

  getWikiFile(projectName: string, relPath: string) {
    return this.http.get<WikiFileContent>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/files/${this.encodeRelPath(relPath)}`
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

  /** The user-defined organisation manifest (themes + hierarchy) over the docs tree. */
  getWikiOrganization(projectName: string) {
    return this.http.get<WikiOrganization>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/organization`
    );
  }

  /** Persist a re-organised manifest; the server sanitises and echoes it back. */
  putWikiOrganization(projectName: string, org: WikiOrganization) {
    return this.http.put<WikiOrganization>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/organization`,
      org
    );
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

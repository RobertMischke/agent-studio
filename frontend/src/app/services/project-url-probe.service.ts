import { HttpClient } from '@angular/common/http';
import { Injectable, Signal, WritableSignal, inject, signal } from '@angular/core';

export type ProjectUrlStatus = 'running' | 'offline' | 'failed' | 'blocked' | 'unknown';
export type ProjectUrlReadinessKind =
  | 'healthy'
  | 'http-error'
  | 'offline'
  | 'timeout'
  | 'frame-blocked'
  | 'unknown';

export interface ProjectUrlReadiness {
  kind: ProjectUrlReadinessKind;
  statusCode: number | null;
  framePolicy: 'allowed' | 'blocked' | 'unknown';
  detail: string | null;
  durationMs: number | null;
}

const UNKNOWN_READINESS: ProjectUrlReadiness = {
  kind: 'unknown',
  statusCode: null,
  framePolicy: 'unknown',
  detail: null,
  durationMs: null,
};

/**
 * On-demand host-side readiness probe for registry-owned Project URLs. The
 * same-origin API can observe the target's HTTP status and response headers,
 * which a browser no-cors fetch deliberately hides.
 */
@Injectable({ providedIn: 'root' })
export class ProjectUrlProbeService {
  private readonly http = inject(HttpClient);
  private readonly states = new Map<string, WritableSignal<ProjectUrlReadiness>>();
  private readonly lastProbed = new Map<string, number>();
  private readonly inflight = new Set<string>();
  private readonly ttlMs = 8000;

  statusFor(projectId: string, urlId: string): ProjectUrlStatus {
    const readiness = this.readinessFor(projectId, urlId);
    switch (readiness.kind) {
      case 'healthy': return 'running';
      case 'http-error': return 'failed';
      case 'frame-blocked': return 'blocked';
      case 'offline':
      case 'timeout': return 'offline';
      default: return 'unknown';
    }
  }

  readinessFor(projectId: string, urlId: string): ProjectUrlReadiness {
    const key = this.key(projectId, urlId);
    const state = this.ensure(key);
    this.maybeProbe(projectId, urlId, key);
    return state();
  }

  signalFor(projectId: string, urlId: string): Signal<ProjectUrlReadiness> {
    return this.ensure(this.key(projectId, urlId));
  }

  refresh(projectId: string, urlId: string): void {
    const key = this.key(projectId, urlId);
    this.ensure(key).set(UNKNOWN_READINESS);
    this.lastProbed.delete(key);
    this.probe(projectId, urlId, key);
  }

  private key(projectId: string, urlId: string): string {
    return `${projectId}::${urlId}`;
  }

  private ensure(key: string): WritableSignal<ProjectUrlReadiness> {
    let state = this.states.get(key);
    if (!state) {
      state = signal<ProjectUrlReadiness>(UNKNOWN_READINESS);
      this.states.set(key, state);
    }
    return state;
  }

  private maybeProbe(projectId: string, urlId: string, key: string): void {
    const last = this.lastProbed.get(key) ?? 0;
    if (Date.now() - last > this.ttlMs) this.probe(projectId, urlId, key);
  }

  private probe(projectId: string, urlId: string, key: string): void {
    if (!projectId || !urlId || this.inflight.has(key)) return;
    this.inflight.add(key);
    this.lastProbed.set(key, Date.now());
    this.http.get<ProjectUrlReadiness>(
      `/api/projects/${encodeURIComponent(projectId)}/urls/${encodeURIComponent(urlId)}/readiness`,
    ).subscribe({
      next: result => {
        this.ensure(key).set(result);
        this.inflight.delete(key);
      },
      error: () => {
        this.ensure(key).set({
          ...UNKNOWN_READINESS,
          kind: 'offline',
          detail: 'The Studio readiness endpoint could not reach this URL.',
        });
        this.inflight.delete(key);
      },
    });
  }
}

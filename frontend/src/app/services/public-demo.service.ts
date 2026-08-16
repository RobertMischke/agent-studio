import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';

/** Server-owned projection of the public read-only edge contract (W34 S4). */
export interface PublicDemoEdgeStatus {
  active: boolean;
  readOnly: boolean;
  profile: string;
  projects: string[];
  allowlistDigest: string;
  allowlistRouteCount: number;
  maxRequestBodyBytes: number;
  requestsPerWindow: number;
  windowSeconds: number;
}

/**
 * Read-only mode for the public demo instance.
 *
 * The flag comes from the server's `/api/environment` payload, which projects
 * the same contract the edge enforces. It is deliberately not a local feature
 * flag: a visitor who flips a browser toggle changes nothing, because the edge
 * refuses every unsafe method regardless of what the UI believes.
 *
 * The UI treatment is explanatory only. Disabled controls tell a visitor why an
 * action is unavailable; the boundary itself lives in the Task Server.
 */
@Injectable({ providedIn: 'root' })
export class PublicDemoService {
  private readonly http = inject(HttpClient);

  private readonly status = signal<PublicDemoEdgeStatus | null>(null);

  /** True once the server has confirmed the public read-only profile. */
  readonly readOnly = computed(() => this.status()?.readOnly === true);

  /** Deployment profile name, or null before the bootstrap read completes. */
  readonly profile = computed(() => this.status()?.profile ?? null);

  /** Seeded demo projects the visitor may read. */
  readonly projects = computed(() => this.status()?.projects ?? []);

  load(): void {
    this.http.get<{ publicDemo?: PublicDemoEdgeStatus }>('/api/environment').subscribe({
      next: (env) => this.status.set(env.publicDemo ?? null),
      error: () => {
        // Leave read-only off. A failed bootstrap read must not disable the
        // operator UI of an ordinary installation.
      },
    });
  }
}

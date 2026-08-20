import { Injectable, signal, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';

export interface PublicDemoFlags {
  active: boolean;
  profile: string | null;
}

/**
 * Reads the public-demo edge flag from /api/environment (the same
 * pre-bootstrap-safe endpoint DevToolsService reads) and exposes it as a
 * signal. Consumed by the read-only banner and by publicDemoGuardInterceptor
 * so both the visible warning and the blocked-mutation behaviour stay driven
 * by one source. The backend edge (PublicDemoEdgeMiddleware) is the actual
 * security boundary - this only drives the "explanatory UX" the W34 dossier
 * calls for (Angular hides/disables controls, it does not enforce anything).
 */
@Injectable({ providedIn: 'root' })
export class PublicDemoModeService {
  private http = inject(HttpClient);

  readonly readOnly = signal(false);

  loadFlags(): void {
    this.http.get<{ publicDemo?: PublicDemoFlags }>('/api/environment').subscribe({
      next: (env) => {
        if (env.publicDemo) this.readOnly.set(!!env.publicDemo.active);
      },
      error: () => {
        /* leave default (not read-only) */
      },
    });
  }
}

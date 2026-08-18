import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { throwError } from 'rxjs';
import { AuthSessionState } from './auth.service';
import { NotificationService } from './notification.service';

/**
 * Client-side companion to the public read-only demo edge (AGT-W34 slice S4).
 *
 * This is explanatory UX, never the boundary. The server denies every mutation
 * on its own and would answer this request with a typed
 * `public-demo-read-only` denial. Refusing it here turns that into an immediate,
 * readable message instead of a round-trip that looks like a bug, and keeps a
 * stray call site from spending the visitor's request budget.
 */
const MUTATING_METHODS = new Set(['POST', 'PUT', 'DELETE', 'PATCH']);

// One toast per burst: a single action can fan out several writes.
const TOAST_THROTTLE_MS = 3000;
let lastToastAt = 0;

/** Test-only reset for the module-level toast throttle. */
export function __resetPublicDemoGuardThrottle(): void {
  lastToastAt = 0;
}

export const publicDemoGuardInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthSessionState);
  const notify = inject(NotificationService);

  const isOwnApi = req.url.startsWith('/api');
  const isMutating = MUTATING_METHODS.has(req.method.toUpperCase());

  if (auth.publicDemo() && isOwnApi && isMutating) {
    const now = Date.now();
    if (now - lastToastAt >= TOAST_THROTTLE_MS) {
      lastToastAt = now;
      notify.warning(
        'This is a public read-only demo. Cards, runs, and settings are shown exactly as they were recorded, and nothing can be changed.',
        'Action blocked — read-only demo',
      );
    }

    return throwError(() => new HttpErrorResponse({
      status: 403,
      statusText: 'Forbidden',
      url: req.url,
      error: {
        error: 'public-demo-read-only',
        message: 'The public demo is read-only. This request was not executed.',
        profile: 'public-demo',
        readOnly: true,
      },
    }));
  }

  return next(req);
};

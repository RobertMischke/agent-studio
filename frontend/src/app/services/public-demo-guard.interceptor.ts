import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { throwError } from 'rxjs';
import { PublicDemoModeService } from './public-demo-mode.service';
import { NotificationService } from './notification.service';

/**
 * Client-side mirror of the backend's public-demo edge policy
 * (PublicDemoEdgeMiddleware / PublicDemoEdgePolicy.ReadOnlyDeniedCode). The
 * server is the real boundary and already rejects every mutating call with
 * "public-demo-read-only" - this interceptor exists so a blocked action fails
 * immediately and visibly instead of round-tripping to the edge first, and so
 * every call site gets the same explanatory toast without threading read-only
 * checks through each component (mirrors offlineGuardInterceptor's shape).
 */
const MUTATING_METHODS = new Set(['POST', 'PUT', 'DELETE', 'PATCH']);
const READ_ONLY_POST_URLS = new Set(['/api/tasks/reference-status']);

const TOAST_THROTTLE_MS = 3000;
let lastToastAt = 0;

/** Test-only reset for the module-level toast throttle. */
export function __resetPublicDemoGuardThrottle(): void {
  lastToastAt = 0;
}

export const publicDemoGuardInterceptor: HttpInterceptorFn = (req, next) => {
  const mode = inject(PublicDemoModeService);
  const notify = inject(NotificationService);

  const isOwnApi = req.url.startsWith('/api');
  const method = req.method.toUpperCase();
  const isReadOnlyPost = method === 'POST' && READ_ONLY_POST_URLS.has(req.url);
  const isMutating = MUTATING_METHODS.has(method) && !isReadOnlyPost;

  if (mode.readOnly() && isOwnApi && isMutating) {
    const now = Date.now();
    if (now - lastToastAt >= TOAST_THROTTLE_MS) {
      lastToastAt = now;
      notify.warning(
        'This is a read-only public demo, so this action was not sent.',
        'Action blocked: read-only demo',
      );
    }

    const error = new HttpErrorResponse({
      status: 403,
      statusText: 'Public demo read-only',
      url: req.url,
      error: { error: 'public-demo-read-only', message: 'This is a read-only public demo.' },
    });
    return throwError(() => error);
  }

  return next(req);
};

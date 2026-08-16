import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { throwError } from 'rxjs';
import { PublicDemoService } from './public-demo.service';

/**
 * Stops mutating calls in the public demo before they leave the browser, so a
 * control that slipped past the disabled state fails with the same explanation
 * the banner gives instead of producing an unexplained 403.
 *
 * This is explanatory UX, not a boundary. The public read-only edge refuses
 * every unsafe method server-side and returns a typed denial; removing this
 * interceptor changes what the visitor reads, never what the server allows.
 * Reads pass through untouched, which is the whole point of the demo.
 */
const MUTATING_METHODS = new Set(['POST', 'PUT', 'DELETE', 'PATCH']);

/** POST used purely to carry a key set in a body. It remains a side-effect-free read. */
const READ_ONLY_POST_URLS = new Set(['/api/tasks/reference-status']);

export const publicDemoReadOnlyInterceptor: HttpInterceptorFn = (req, next) => {
  const demo = inject(PublicDemoService);

  const method = req.method.toUpperCase();
  const isMutating = MUTATING_METHODS.has(method) && !READ_ONLY_POST_URLS.has(req.url);

  if (demo.readOnly() && req.url.startsWith('/api') && isMutating) {
    return throwError(
      () =>
        new HttpErrorResponse({
          status: 403,
          statusText: 'Public demo is read-only',
          url: req.url,
          error: {
            error: 'public-demo-read-only',
            message: 'This is a read-only public demo. Changes are not accepted.',
          },
        }),
    );
  }

  return next(req);
};

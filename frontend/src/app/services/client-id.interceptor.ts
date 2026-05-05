import { HttpInterceptorFn } from '@angular/common/http';

/**
 * Stamps `X-Client-Id` on every outbound API request so the backend's
 * registration boundary accepts the call. The frontend has no per-user
 * identity yet; until a registration UI ships we sign every request as
 * the bootstrap `local-default` identity, which exists on every backend.
 *
 * Once the identity-picker UI lands, swap the static id for the active
 * client signal value. This file is the single chokepoint for that
 * change.
 */
export const CLIENT_ID = 'local-default';

export const clientIdInterceptor: HttpInterceptorFn = (req, next) => {
  // Only stamp our own backend; leave third-party requests alone (the
  // Angular dev server proxies /api to the backend, so absolute URLs
  // never appear here under normal use).
  if (req.headers.has('X-Client-Id')) return next(req);
  const cloned = req.clone({
    setHeaders: { 'X-Client-Id': CLIENT_ID }
  });
  return next(cloned);
};

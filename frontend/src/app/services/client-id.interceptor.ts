import { HttpInterceptorFn } from '@angular/common/http';

/**
 * Stamps the local profile's attribution id on outbound API requests. In the
 * networked profile this header remains attribution only: the secure server
 * session is the human authentication boundary and Angular stores no reusable
 * password or bearer secret.
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

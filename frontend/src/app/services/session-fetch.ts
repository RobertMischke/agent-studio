import { CLIENT_ID } from './client-id.interceptor';

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

// Scheme-aware CSRF cookie: the __Host- prefix is only settable over HTTPS; the
// bare name is what the backend writes over local HTTP dev. Mirror the exact
// pair the sessionSecurityInterceptor reads so Fetch uploads and HttpClient XHR
// agree on the same proof.
const CSRF_COOKIE_NAMES = ['__Host-agentstudio-csrf=', 'agentstudio-csrf='];

/** Every input shape the Fetch API itself accepts. */
export type SessionFetchInput = string | URL | Request;

/**
 * Applies the same session, CSRF, and attribution policy as Angular's HTTP
 * interceptor chain (offline guard aside) to the few same-origin multipart
 * uploads that must use the Fetch API directly - FormData streaming that
 * HttpClient does not express cleanly.
 *
 * Accepts every input the Fetch API accepts - a string path, an absolute URL
 * string, a {@link URL}, or a {@link Request} - and resolves it against the
 * document origin. The earlier implementation typed `input` as a bare string
 * and called `input.startsWith('/api/')`, which threw a `TypeError` on a URL or
 * Request object and wrongly rejected same-origin absolute URLs; both are now
 * handled. Session proofs are only ever attached to a same-origin `/api`
 * request; a cross-origin or non-`/api` target is refused so credentials and
 * the CSRF token never leak off-origin.
 */
export function sessionFetch(input: SessionFetchInput, init: RequestInit = {}): Promise<Response> {
  const target = resolveTargetUrl(input);
  if (target === null || !isSameOriginApiRequest(target)) {
    throw new TypeError('sessionFetch accepts same-origin /api requests only.');
  }
  return fetch(input, secureSessionRequest(init, cookieHeader(), input));
}

/**
 * Pure header/credential policy, split out so it is unit-testable without a
 * network. Merges attribution (`X-Client-Id`) and, for unsafe methods, the CSRF
 * token onto whatever headers the caller - and, for a {@link Request} input, the
 * Request itself - already set, never clobbering an explicit value. Exported for
 * spec coverage and for callers that assemble their own `fetch` call.
 */
export function secureSessionRequest(
  init: RequestInit = {},
  cookieHeader = '',
  input?: SessionFetchInput,
): RequestInit {
  const method = (init.method
    ?? (input instanceof Request ? input.method : undefined)
    ?? 'GET').toUpperCase();

  // Seed from the Request's own headers (if any), then let the init headers win.
  const headers = new Headers(input instanceof Request ? input.headers : undefined);
  new Headers(init.headers).forEach((value, key) => headers.set(key, value));

  if (!headers.has('X-Client-Id')) headers.set('X-Client-Id', CLIENT_ID);
  if (!SAFE_METHODS.has(method) && !headers.has('X-CSRF-Token')) {
    const csrf = readCsrfCookie(cookieHeader);
    if (csrf) headers.set('X-CSRF-Token', decodeURIComponent(csrf));
  }
  return { ...init, credentials: 'same-origin', headers };
}

function resolveTargetUrl(input: SessionFetchInput): URL | null {
  const base = typeof location !== 'undefined' && location.href ? location.href : 'http://localhost/';
  try {
    if (typeof input === 'string') return new URL(input, base);
    if (input instanceof URL) return input;
    if (input instanceof Request) return new URL(input.url, base);
    return null;
  } catch {
    // A malformed input is not a valid same-origin /api target.
    return null;
  }
}

function isSameOriginApiRequest(target: URL): boolean {
  const sameOrigin =
    typeof location === 'undefined' || !location.origin || target.origin === location.origin;
  return sameOrigin && target.pathname.startsWith('/api/');
}

function cookieHeader(): string {
  return typeof document === 'undefined' ? '' : document.cookie;
}

function readCsrfCookie(cookieHeader: string): string | null {
  const parts = cookieHeader.split(';').map((value) => value.trim());
  for (const name of CSRF_COOKIE_NAMES) {
    const hit = parts.find((value) => value.startsWith(name));
    if (hit) return hit.slice(name.length) || null;
  }
  return null;
}

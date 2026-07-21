import { afterEach, describe, expect, it, vi } from 'vitest';
import { secureSessionRequest, sessionFetch } from './session-fetch';

describe('secureSessionRequest', () => {
  it('adds session credentials, attribution, and CSRF to multipart mutations', () => {
    const secured = secureSessionRequest(
      { method: 'POST', body: new FormData() },
      'theme=dark; __Host-agentstudio-csrf=csrf%2Dproof',
    );
    const headers = new Headers(secured.headers);

    expect(secured.credentials).toBe('same-origin');
    expect(headers.get('X-Client-Id')).toBe('local-default');
    expect(headers.get('X-CSRF-Token')).toBe('csrf-proof');
  });

  it('reads the scheme-agnostic CSRF cookie the backend sets over plain HTTP dev', () => {
    const secured = secureSessionRequest({ method: 'POST' }, 'agentstudio-csrf=dev%2Dproof');
    expect(new Headers(secured.headers).get('X-CSRF-Token')).toBe('dev-proof');
  });

  it('does not add a CSRF header to safe reads', () => {
    const secured = secureSessionRequest({ method: 'GET' }, '__Host-agentstudio-csrf=csrf-proof');
    expect(new Headers(secured.headers).has('X-CSRF-Token')).toBe(false);
  });

  it('never clobbers a caller-supplied header', () => {
    const secured = secureSessionRequest(
      { method: 'POST', headers: { 'X-Client-Id': 'explicit-id' } },
      '__Host-agentstudio-csrf=csrf-proof',
    );
    expect(new Headers(secured.headers).get('X-Client-Id')).toBe('explicit-id');
  });

  it('merges the headers of a Request input and derives its method', () => {
    // The undici Request constructor requires an absolute URL; the browser
    // resolves relative ones. sessionFetch itself accepts a relative string
    // path (see resolveTargetUrl) - this only exercises the header/method merge.
    const request = new Request(new URL('/api/tasks/x/attachments', location.origin), {
      method: 'POST',
      headers: { 'X-Trace': 'abc' },
    });
    const secured = secureSessionRequest({}, '__Host-agentstudio-csrf=csrf-proof', request);
    const headers = new Headers(secured.headers);

    expect(headers.get('X-Trace')).toBe('abc');
    // POST derived from the Request means the CSRF token is attached.
    expect(headers.get('X-CSRF-Token')).toBe('csrf-proof');
  });

  it('tolerates an empty cookie header without throwing', () => {
    const secured = secureSessionRequest({ method: 'POST' }, '');
    expect(new Headers(secured.headers).has('X-CSRF-Token')).toBe(false);
    expect(secured.credentials).toBe('same-origin');
  });
});

describe('sessionFetch input handling', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('refuses to forward session proofs to a different origin', () => {
    expect(() => sessionFetch('https://example.test/upload', { method: 'POST' })).toThrow(
      'same-origin /api',
    );
  });

  it('refuses a same-origin non-/api path', () => {
    expect(() => sessionFetch('/not-api/thing', { method: 'POST' })).toThrow('same-origin /api');
  });

  // The pre-fix implementation typed input as a bare string and threw a
  // TypeError ("input.startsWith is not a function") on these otherwise valid
  // Fetch inputs. They must now resolve to the same-origin /api target and issue
  // the request with the session headers applied.
  it('accepts a relative /api string path', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('ok'));
    vi.stubGlobal('fetch', fetchMock);

    await sessionFetch('/api/tasks/x/attachments', { method: 'POST', body: new FormData() });

    expect(fetchMock).toHaveBeenCalledOnce();
    const init = fetchMock.mock.calls[0][1] as RequestInit;
    expect(new Headers(init.headers).get('X-Client-Id')).toBe('local-default');
    expect(init.credentials).toBe('same-origin');
  });

  it('accepts a same-origin absolute URL string', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('ok'));
    vi.stubGlobal('fetch', fetchMock);

    const absolute = new URL('/api/tasks/x/attachments', location.origin).href;
    await sessionFetch(absolute, { method: 'POST', body: new FormData() });

    expect(fetchMock).toHaveBeenCalledOnce();
    expect(fetchMock.mock.calls[0][0]).toBe(absolute);
  });

  it('accepts a URL object', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('ok'));
    vi.stubGlobal('fetch', fetchMock);

    const url = new URL('/api/tasks/x/attachments', location.origin);
    await sessionFetch(url, { method: 'POST', body: new FormData() });

    expect(fetchMock).toHaveBeenCalledOnce();
    expect(fetchMock.mock.calls[0][0]).toBe(url);
  });

  it('accepts a Request object', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('ok'));
    vi.stubGlobal('fetch', fetchMock);

    const request = new Request(new URL('/api/tasks/x/attachments', location.origin), {
      method: 'POST',
    });
    await sessionFetch(request);

    expect(fetchMock).toHaveBeenCalledOnce();
    expect(fetchMock.mock.calls[0][0]).toBe(request);
  });
});

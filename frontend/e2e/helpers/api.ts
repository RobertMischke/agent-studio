/**
 * Tiny REST client for the backend at http://localhost:5030.
 * Used by Playwright specs for setup / teardown / polling so we don't have
 * to drive the UI for things the API can do directly.
 */

export const BACKEND = 'http://localhost:5030';

export async function api<T = unknown>(
  path: string,
  init: RequestInit = {}
): Promise<T> {
  const res = await fetch(`${BACKEND}${path}`, {
    headers: { 'content-type': 'application/json', ...(init.headers ?? {}) },
    ...init
  });
  const text = await res.text();
  if (!res.ok) {
    throw new Error(`API ${init.method ?? 'GET'} ${path} -> ${res.status} ${res.statusText}\n${text}`);
  }
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

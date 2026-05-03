/**
 * Tiny REST client for the backend.
 * Defaults to the dev backend at http://localhost:5030. Agents driving the
 * stable stack set `PW_TARGET=stable` (-> http://localhost:5031) or pin an
 * explicit URL via `PW_BACKEND_URL=http://...`. Same precedence table as
 * playwright.config.ts so a single env var flips both layers.
 *
 * Used by Playwright specs for setup / teardown / polling so we don't have
 * to drive the UI for things the API can do directly.
 */

const apiTarget = (process.env.PW_TARGET ?? 'dev').toLowerCase();
export const BACKEND =
  process.env.PW_BACKEND_URL?.trim()
  || (apiTarget === 'stable' ? 'http://localhost:5031' : 'http://localhost:5030');

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

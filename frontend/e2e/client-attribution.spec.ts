import { test, expect } from '@playwright/test';
import { api, BACKEND } from './helpers/api';

/**
 * Client identity + per-task attribution.
 *
 * Locks the user-visible contract for the registration boundary:
 * - the bootstrap `local-default` identity exists on every fresh backend;
 * - registering a new identity returns a kebab-case id;
 * - mutations from an unknown X-Client-Id are rejected with 401;
 * - a job created with `ownerClientId` renders an owner chip on its card;
 * - the top-bar Owner filter narrows the board to a single client.
 */

interface ClientSummary {
  id: string;
  displayName: string;
  emoji: string | null;
  colour: string | null;
  kind: string;
}

interface WatchPath { name: string; path: string; rootPath: string; }

const TEST_PREFIX = 'e2e-owner-';

async function ensureWatchPath(): Promise<WatchPath> {
  const list = await api<WatchPath[]>('/api/watch-paths');
  expect(list.length).toBeGreaterThan(0);
  return list[0];
}

async function registerClient(displayName: string, emoji: string, colour: string): Promise<ClientSummary> {
  return api<ClientSummary>('/api/clients/register', {
    method: 'POST',
    body: JSON.stringify({ displayName, emoji, colour, kind: 'human' })
  });
}

test.describe('Client identity + attribution', () => {

  test('bootstrap local-default identity exists', async () => {
    const all = await api<ClientSummary[]>('/api/clients/');
    expect(all.find(c => c.id === 'local-default')).toBeTruthy();
  });

  test('mutations without X-Client-Id are rejected with 401', async () => {
    const res = await fetch(`${BACKEND}/api/jobs`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' }, // no X-Client-Id
      body: JSON.stringify({ title: 'no-header', agent: 'copilot', watchPath: '' })
    });
    expect(res.status).toBe(401);
    const body = await res.json();
    expect(body.error).toBe('client-unknown');
  });

  test('mutations with unknown X-Client-Id are rejected with 401', async () => {
    const res = await fetch(`${BACKEND}/api/jobs`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-client-id': 'ghost-' + Date.now() },
      body: JSON.stringify({ title: 'ghost', agent: 'copilot', watchPath: '' })
    });
    expect(res.status).toBe(401);
  });

  test('registration returns a kebab-case id and is idempotent on displayName', async () => {
    const a = await registerClient(TEST_PREFIX + 'Alpha', '🦊', '#7c3aed');
    expect(a.id).toBe(TEST_PREFIX + 'alpha');
    const b = await registerClient(TEST_PREFIX + 'Alpha', '🐺', '#16a34a');
    expect(b.id).toBe(a.id);
    expect(b.emoji).toBe('🐺'); // refresh accepted
  });

  test('owner chip and client filter on the board', async ({ page }) => {
    const watch = await ensureWatchPath();
    const ownerA = await registerClient(TEST_PREFIX + 'Card A Owner', '🦊', '#7c3aed');
    const ownerB = await registerClient(TEST_PREFIX + 'Card B Owner', '🐢', '#16a34a');

    // Two jobs, one per owner. Land in 1-preparation (default) so they appear
    // immediately on the board without triggering a runner pickup.
    const titleA = `Owner-Chip-A-${Date.now()}`;
    const titleB = `Owner-Chip-B-${Date.now()}`;
    await api('/api/jobs/', {
      method: 'POST',
      body: JSON.stringify({
        title: titleA,
        agent: 'copilot',
        watchPath: watch.path,
        ownerClientId: ownerA.id,
        targetState: '1-preparation'
      })
    });
    await api('/api/jobs/', {
      method: 'POST',
      body: JSON.stringify({
        title: titleB,
        agent: 'copilot',
        watchPath: watch.path,
        ownerClientId: ownerB.id,
        targetState: '1-preparation'
      })
    });

    await page.goto('/');

    // Both owner chips render on their respective cards.
    const cardA = page.locator('[data-testid="job-card"]', { hasText: titleA });
    const cardB = page.locator('[data-testid="job-card"]', { hasText: titleB });
    await expect(cardA).toBeVisible();
    await expect(cardB).toBeVisible();

    const chipA = cardA.locator('[data-testid="job-card-owner"]');
    const chipB = cardB.locator('[data-testid="job-card-owner"]');
    await expect(chipA).toBeVisible();
    await expect(chipB).toBeVisible();
    await expect(chipA).toContainText(ownerA.displayName);
    await expect(chipB).toContainText(ownerB.displayName);
    await expect(chipA).toHaveAttribute('data-owner-id', ownerA.id);
    await expect(chipB).toHaveAttribute('data-owner-id', ownerB.id);

    // Screenshot evidence: two cards owned by different clients.
    await page.screenshot({ path: 'test-results/client-attribution-two-owners.png', fullPage: true });

    // Filter narrows the board to the chosen client.
    const filter = page.getByTestId('client-filter-select');
    await filter.selectOption(ownerA.id);
    await expect(cardA).toBeVisible();
    await expect(cardB).toHaveCount(0);

    await filter.selectOption(ownerB.id);
    await expect(cardB).toBeVisible();
    await expect(cardA).toHaveCount(0);

    await filter.selectOption('');
    await expect(cardA).toBeVisible();
    await expect(cardB).toBeVisible();
  });

  test.afterAll(async () => {
    // Best-effort cleanup of the e2e-owner clients. Soft-delete only;
    // historical attribution is preserved by design, so the records stay
    // (kind=retired) and the next run re-uses the same ids.
    const all = await api<ClientSummary[]>('/api/clients/');
    for (const c of all) {
      if (c.id.startsWith(TEST_PREFIX) && c.kind !== 'retired') {
        try {
          await api(`/api/clients/${c.id}`, { method: 'DELETE' });
        } catch { /* ignore */ }
      }
    }
  });
});

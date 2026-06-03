import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, getJob } from '../helpers/jobs';

/** Mirrors the backend/FE `TaskReferences` shape: four relation kinds, each a
 *  list of F33 stable keys. Replace-all write target for PUT /references. */
interface TaskReferences {
  dependsOn: string[];
  relatedTo: string[];
  blockedBy: string[];
  supersedes: string[];
}

/**
 * F34 cross-references live INSIDE the Overview tab (compact chip section),
 * not in a standalone full-width bar above the prompt-pane tabs.
 *
 * Acceptance (POLISH "References kompakt in Overview integrieren"):
 *  - the References section renders inside the Overview pane, never as its own
 *    full-width strip outside the panes;
 *  - all four relation kinds (dependsOn / relatedTo / blockedBy / supersedes)
 *    stay visible and navigable;
 *  - the section is a quiet chip list by default, with edit (add/remove) behind
 *    a text-only toggle.
 *
 * Self-contained: seeds one subject task plus four target tasks (so every
 * relation kind has a real, existing key to point at), drives the live backend,
 * and deletes every fixture in afterAll. Runs against dev or stable per
 * PW_TARGET / PW_BACKEND_URL (same precedence as the api helper).
 */

const WP_KEY = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

interface WatchPath {
  name: string;
  path: string;
}

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  // Prefer the agent-taskboard project (where F33 keys are minted) when present.
  return paths.find((p) => p.path === WP_KEY)?.path ?? paths[0].path;
}

async function deleteTask(id: string, watchPath: string): Promise<void> {
  try {
    await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE',
    });
  } catch {
    // best-effort teardown
  }
}

async function setReferences(id: string, watchPath: string, refs: TaskReferences): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(id)}/references?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'PUT',
    body: JSON.stringify(refs),
  });
}

async function openOverview(page: Page, id: string, watchPath: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
  // The Overview tab is the default tab on a freshly opened task detail.
  await expect(page.getByTestId('prompt-tab-overview')).toHaveAttribute('aria-selected', 'true', {
    timeout: 15_000,
  });
}

test.describe('Cross-references — compact inside Overview', () => {
  let watchPath: string;
  let subjectId: string;
  const targetIds: string[] = [];
  // kind -> target F33 key, filled in beforeAll once fixtures get their keys.
  const targetKeyByKind: Record<keyof TaskReferences, string> = {
    dependsOn: '',
    relatedTo: '',
    blockedBy: '',
    supersedes: '',
  };

  test.beforeAll(async () => {
    watchPath = await pickWatchPath();

    const subject = await createJob({
      title: 'zz e2e refs subject',
      watchPath,
      targetState: '2-ready',
    });
    subjectId = subject.id;

    const kinds: (keyof TaskReferences)[] = ['dependsOn', 'relatedTo', 'blockedBy', 'supersedes'];
    for (const kind of kinds) {
      const t = await createJob({
        title: `zz e2e refs target ${kind}`,
        watchPath,
        targetState: '2-ready',
      });
      targetIds.push(t.id);
      const info = await getJob(t.id, watchPath);
      // The F33 stable key (e.g. ASS-630) is what references point at, distinct
      // from the composite `jobKey` (watchPath::id). getJob's narrow Job type
      // omits it, so read it off the raw payload.
      const f33Key = (info as { key?: string }).key ?? '';
      if (!f33Key) throw new Error(`target ${t.id} did not get an F33 key`);
      targetKeyByKind[kind] = f33Key;
    }

    await setReferences(subjectId, watchPath, {
      dependsOn: [targetKeyByKind.dependsOn],
      relatedTo: [targetKeyByKind.relatedTo],
      blockedBy: [targetKeyByKind.blockedBy],
      supersedes: [targetKeyByKind.supersedes],
    });
  });

  test.afterAll(async () => {
    for (const id of [subjectId, ...targetIds]) {
      if (id) await deleteTask(id, watchPath);
    }
  });

  test('references render inside the Overview pane, not as a full-width bar', async ({ page }) => {
    await openOverview(page, subjectId, watchPath);

    const overview = page.locator('app-overview-pane');
    await expect(overview).toBeVisible({ timeout: 15_000 });

    // The section exists and is a DESCENDANT of the Overview pane.
    const sectionInOverview = overview.getByTestId('references-section');
    await expect(sectionInOverview).toBeVisible({ timeout: 10_000 });

    // There is exactly one References section in the whole document, and it is
    // the one inside Overview (no stray full-width bar elsewhere).
    await expect(page.getByTestId('references-section')).toHaveCount(1);

    // It must NOT be a sibling above the prompt-pane tabs: assert it is not a
    // descendant of the detail header / tablist region.
    const sectionAboveTabs = page.locator('[data-testid="pane-prompt-header"] [data-testid="references-section"]');
    await expect(sectionAboveTabs).toHaveCount(0);
  });

  test('all four relation kinds are visible with navigable chips', async ({ page }) => {
    await openOverview(page, subjectId, watchPath);
    const overview = page.locator('app-overview-pane');

    await expect(overview.getByTestId('references-count')).toHaveText('4');

    const kinds: (keyof TaskReferences)[] = ['dependsOn', 'relatedTo', 'blockedBy', 'supersedes'];
    for (const kind of kinds) {
      await expect(overview.getByTestId(`references-row-${kind}`)).toBeVisible();
      const chip = overview.getByTestId(`reference-chip-${targetKeyByKind[kind]}`);
      await expect(chip).toBeVisible();
      // Chip carries a clickable link (navigation target).
      await expect(chip.locator('.refs__chip-link')).toBeEnabled();
    }
  });

  test('edit mode reveals add inputs for every relation kind', async ({ page }, testInfo) => {
    await openOverview(page, subjectId, watchPath);
    const overview = page.locator('app-overview-pane');

    // Capture the calm default (read-only chips) before entering edit mode.
    const shot = testInfo.outputPath('references-overview.png');
    await overview.screenshot({ path: shot });
    await testInfo.attach('references-overview.png', { path: shot, contentType: 'image/png' });

    await overview.getByTestId('references-add-toggle').click();

    const kinds: (keyof TaskReferences)[] = ['dependsOn', 'relatedTo', 'blockedBy', 'supersedes'];
    for (const kind of kinds) {
      await expect(overview.getByTestId(`reference-add-${kind}`)).toBeVisible();
    }
  });
});

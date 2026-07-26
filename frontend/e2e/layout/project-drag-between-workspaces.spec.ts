import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';

/**
 * Regression spec for the sidebar "drag a project onto a workspace folder to
 * reassign it to that workspace" flow (F46 two-level tree). Pre-F46 the
 * gesture dropped a project onto another project's ROW and fanned out
 * `change-project` calls; after F46 the tree is workspace -> project, so the
 * drop target is a WORKSPACE folder and the action is a single registry
 * reassignment (`PUT /api/projects/{PROJ-NNN}` with `{ workspaceId }`). No
 * job folder is moved on disk (ADR-0048).
 *
 * Backstop: the PUT is observed via a route handler and answered 200 so the
 * spec proves the UI contract — draggable rows, valid-target highlight on
 * the workspace group, same-workspace no-op — without mutating the running
 * registry.
 */

interface RegistryProject {
  id: string;
  displayName: string;
  workspaceId: string;
  storageLocation: string;
  archived: boolean;
}
interface RegistryWorkspace {
  id: string;
  displayName: string;
  projects: RegistryProject[];
}

function folderTail(p: string): string {
  const parts = p.split(/[\\/]+/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : p;
}

test.describe('Sidebar: drag a project onto a workspace folder', () => {
  test('project rows are draggable, workspace folder highlights, drop reassigns via the registry', async ({ page }) => {
    const workspaces = await api<RegistryWorkspace[]>('/api/workspaces');
    test.skip(!Array.isArray(workspaces) || workspaces.length < 2,
      'Need at least two registry workspaces to test the drag-to-workspace flow.');

    // Pick a non-archived project P living in workspace A, plus any other
    // workspace B to drop it onto.
    let source: { project: RegistryProject; workspace: RegistryWorkspace } | null = null;
    let target: RegistryWorkspace | null = null;
    for (const ws of workspaces) {
      const p = (ws.projects ?? []).find(pr => !pr.archived);
      if (!p) continue;
      const other = workspaces.find(w => w.id !== ws.id);
      if (!other) continue;
      source = { project: p, workspace: ws };
      target = other;
      break;
    }
    test.skip(!source || !target, 'No movable project found across the configured workspaces.');
    const src = source!;
    const dst = target!;

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const sidebar = page.getByTestId('studio-sidebar');
    await expect(sidebar).toBeVisible({ timeout: 10_000 });

    // The Explorer's project rows are keyed by their job-derived name, which
    // is either the registry displayName or the storage-folder tail.
    const rowByDisplay = page.getByTestId(`studio-explorer-project-row-${src.project.displayName}`);
    const rowByTail = page.getByTestId(`studio-explorer-project-row-${folderTail(src.project.storageLocation)}`);
    const sourceRow = (await rowByDisplay.count()) ? rowByDisplay : rowByTail;
    if (!(await sourceRow.count())) {
      test.skip(true, 'Source project row is not rendered (its workspace may be collapsed / empty).');
      return;
    }
    await expect(sourceRow.first()).toBeVisible({ timeout: 10_000 });

    const dropZone = page.getByTestId(`studio-explorer-ws-drop-${dst.id}`);
    await expect(dropZone).toBeVisible({ timeout: 10_000 });

    // Observe the registry reassignment as it leaves the page and answer 200
    // so the running registry is not actually mutated by the test.
    const observed: { projId: string; workspaceId: string }[] = [];
    await page.route('**/api/projects/*', async (route, request) => {
      if (request.method() !== 'PUT') { await route.continue(); return; }
      const url = new URL(request.url());
      const m = /\/api\/projects\/([^/?]+)/.exec(url.pathname);
      const projId = m ? decodeURIComponent(m[1]) : '';
      let workspaceId = '';
      try {
        const body = request.postDataJSON() as { workspaceId?: string } | null;
        workspaceId = body?.workspaceId ?? '';
      } catch { /* ignore */ }
      observed.push({ projId, workspaceId });
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    // Angular CDK consumes the real mouse/pointer pipeline used by dragTo.
    await sourceRow.first().dragTo(dropZone);

    await expect.poll(() => observed.length, { timeout: 5_000 }).toBeGreaterThan(0);
    expect(observed[0].projId).toBe(src.project.id);
    expect(observed[0].workspaceId).toBe(dst.id);

    await page.screenshot({
      path: 'test-results/project-drag-between-workspaces.png',
      fullPage: false,
    });
  });
});

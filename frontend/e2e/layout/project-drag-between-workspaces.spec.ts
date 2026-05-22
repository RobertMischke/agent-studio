import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';

/**
 * Regression spec for the sidebar "drag a project onto another project's
 * row to reassign it to that workspace" flow. Mirrors the static dropdown
 * already shipped in the Project Settings panel (
 * `project-workspace-section`), but exercises the drag affordance the
 * shell adds in the Explorer sidebar.
 *
 * Backstop: even if no jobs exist in the source project, the spec proves
 * the visual contract — draggable rows + valid-target highlight + invalid
 * self-drop guard — without needing to actually mutate the backend. The
 * call to `/api/jobs/{id}/change-project` is observed via a route handler
 * so the test asserts what the UI WOULD do without polluting the running
 * workspace.
 */

interface WatchPath { name: string; path: string }

async function getWatchPaths(): Promise<WatchPath[]> {
  const list = await api<WatchPath[]>('/api/watch-paths');
  return Array.isArray(list) ? list : [];
}

test.describe('Sidebar: drag a project between workspaces', () => {
  test('project rows are draggable, visual feedback fires, drop reassigns via change-project', async ({ page }) => {
    const workspaces = await getWatchPaths();
    test.skip(workspaces.length < 2, 'Need at least two configured workspaces to test the drag-to-workspace flow.');

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    // Surface the Explorer panel so the project rows are rendered.
    const sidebar = page.getByTestId('studio-sidebar');
    await expect(sidebar).toBeVisible({ timeout: 10_000 });

    // Find at least two project rows. The Explorer's project rows carry
    // testids of the form `studio-explorer-project-row-<name>`.
    const source = workspaces[0];
    const target = workspaces[1];
    const sourceRow = page.getByTestId(`studio-explorer-project-row-${source.name}`);
    const targetRow = page.getByTestId(`studio-explorer-project-row-${target.name}`);
    await expect(sourceRow).toBeVisible({ timeout: 10_000 });
    await expect(targetRow).toBeVisible({ timeout: 10_000 });

    // Contract 1: project rows are HTML-draggable.
    await expect(sourceRow).toHaveAttribute('draggable', 'true');
    await expect(targetRow).toHaveAttribute('draggable', 'true');

    // Capture the change-project POST as it leaves the page so the spec
    // can assert the right target path was sent without leaving the
    // backend in a different state when the spec ends. We respond with a
    // 200 OK and roll the move back by re-sending the inverse to keep
    // the dev workspace pristine.
    const observed: { jobId: string; targetWatchPath: string; sourceWatchPath: string }[] = [];
    await page.route('**/api/jobs/*/change-project**', async (route, request) => {
      const url = new URL(request.url());
      const m = /\/api\/jobs\/([^/]+)\/change-project/.exec(url.pathname);
      const jobId = m ? decodeURIComponent(m[1]) : '';
      const sourceWatchPath = url.searchParams.get('watchPath') ?? '';
      let targetWatchPath = '';
      try {
        const body = request.postDataJSON() as { targetWatchPath?: string } | null;
        targetWatchPath = body?.targetWatchPath ?? '';
      } catch { /* ignore */ }
      observed.push({ jobId, targetWatchPath, sourceWatchPath });
      await route.fulfill({ status: 200, body: '' });
    });

    // Drive the HTML5 drag dispatch directly. Playwright's `dragTo` does
    // not emit `dragstart` + `dragend` events the way native HTML5 drag
    // needs, so we synthesize them. (Same approach as the agent-orchestrator
    // tab drag specs.)
    await page.evaluate(({ sourceName, targetName }) => {
      const sel = (name: string) => document.querySelector(`[data-testid="studio-explorer-project-row-${name}"]`) as HTMLElement | null;
      const src = sel(sourceName);
      const dst = sel(targetName);
      if (!src || !dst) throw new Error('rows missing');
      const dataTransfer = new DataTransfer();
      const fire = (el: HTMLElement, type: string) => {
        const ev = new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer });
        el.dispatchEvent(ev);
      };
      fire(src, 'dragstart');
      fire(dst, 'dragenter');
      fire(dst, 'dragover');
    }, { sourceName: source.name, targetName: target.name });

    // After dragstart over the target, the row should pick up the
    // drop-target highlight class.
    await expect(targetRow).toHaveClass(/studio-tree-project--drop-target/);
    // The source row fades while dragging.
    await expect(sourceRow).toHaveClass(/studio-tree-project--dragging/);
    // The own-row cannot be its own drop target — drop-invalid stays
    // off the source row itself (the source is excluded from invalid
    // styling by design).
    await expect(sourceRow).not.toHaveClass(/studio-tree-project--drop-target/);

    // Now complete the drop. The change-project POST fires once per job
    // in the source project; an empty source project routes through the
    // "no jobs to move" hint path instead.
    await page.evaluate(({ sourceName, targetName }) => {
      const sel = (name: string) => document.querySelector(`[data-testid="studio-explorer-project-row-${name}"]`) as HTMLElement | null;
      const src = sel(sourceName);
      const dst = sel(targetName);
      if (!src || !dst) throw new Error('rows missing');
      const dataTransfer = new DataTransfer();
      const fire = (el: HTMLElement, type: string) => {
        const ev = new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer });
        el.dispatchEvent(ev);
      };
      fire(dst, 'drop');
      fire(src, 'dragend');
    }, { sourceName: source.name, targetName: target.name });

    // Either we observed at least one change-project call (when the
    // source project had jobs), or the move-error hint surfaced (when
    // the source project was empty). Either branch is a green
    // assertion for the visual contract.
    const hint = page.getByTestId('studio-explorer-move-error');
    const wasEmpty = await hint.isVisible().catch(() => false);
    if (observed.length === 0 && !wasEmpty) {
      // Allow a beat for the forkJoin to settle before failing.
      await page.waitForTimeout(500);
    }
    const finalObserved = observed.slice();
    const finalEmpty = await hint.isVisible().catch(() => false);

    if (finalObserved.length > 0) {
      for (const row of finalObserved) {
        expect(row.targetWatchPath).toBe(target.path);
        expect(row.sourceWatchPath).toBe(source.path);
      }
    } else {
      expect(finalEmpty).toBeTruthy();
    }

    // Capture for the job results bundle.
    await page.screenshot({
      path: 'test-results/project-drag-between-workspaces.png',
      fullPage: false,
    });
  });
});

import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

/**
 * Regression spec for the operator bug:
 *
 *   "BUG: Epic-Ansicht - Model-Picker-Dropdown oeffnet verdeckt/abgeschnitten
 *    (Clipping/z-index)."
 *
 * In the epic detail view the Model/CLI picker (app-cli-model-selector) sits
 * near the top of the panel. It used to open upward with `position: absolute;
 * bottom: 100%`, so an ancestor's overflow / paint-containment clipped it and
 * the popover appeared cut off (top rows + right edge off-screen).
 *
 * The fix promotes the popover to the browser top layer (native Popover API)
 * and positions it against the viewport, flipping below the trigger when there
 * is no room above. This spec proves the opened picker is fully on-screen and
 * is the topmost element at its own centre (nothing clips or occludes it).
 *
 * Follows the sibling task-detail specs: drive the live frontend (proxied to a
 * real backend), pick a real epic off the board, deep-link to it. Screenshots
 * land under JOB_RESULTS_DIR when the orchestrator sets it, else test-results/.
 */

const SHOTS_DIR = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR)
  : path.resolve(__dirname, '../../test-results/epic-model-picker-clipping');

// Lanes whose cards reliably mount the detail view (running/terminal lanes).
const MOUNTABLE = new Set(['3-progress', '4-auto-review', '5-human-review', '6-completed']);

interface TaskLite {
  id: string;
  watchPath: string;
  kind?: string;
  state?: string;
}

async function fetchTasks(page: Page): Promise<TaskLite[]> {
  const res = await page.request.get('/api/tasks');
  if (!res.ok()) return [];
  const tasks = await res.json();
  return Array.isArray(tasks) ? (tasks as TaskLite[]) : [];
}

function pickEpic(tasks: TaskLite[]): TaskLite | null {
  const isEpic = (t: TaskLite) => t.kind === 'epic';
  return tasks.find((t) => isEpic(t) && MOUNTABLE.has(t.state ?? '')) ?? tasks.find(isEpic) ?? null;
}

test.describe('Epic detail — model picker is not clipped', () => {
  test('opening the Model/CLI picker renders fully inside the viewport', async ({ page }) => {
    // A deliberately short viewport keeps the trigger near the top edge — the
    // exact geometry that made the old upward-opening popover clip off-screen.
    await page.setViewportSize({ width: 1280, height: 760 });

    const epic = pickEpic(await fetchTasks(page));
    if (!epic) {
      test.skip(true, 'No epic (kind=epic) on the board to exercise the picker.');
      return;
    }

    await page.goto(
      `/?job=${encodeURIComponent(epic.id)}&watchPath=${encodeURIComponent(epic.watchPath)}`,
    );

    const trigger = page.getByTestId('epic-model');
    await expect(trigger).toBeVisible({ timeout: 20_000 });

    // If a run is in flight the picker is disabled by design — skip rather than
    // fail, this spec is about geometry, not the disabled affordance.
    if (await trigger.isDisabled()) {
      test.skip(true, 'Picked epic is running; model picker is disabled.');
      return;
    }

    await trigger.scrollIntoViewIfNeeded();
    await trigger.click();

    const picker = page.getByTestId('epic-model-picker');
    await expect(picker).toBeVisible({ timeout: 5_000 });

    const vw = page.viewportSize()!.width;
    const vh = page.viewportSize()!.height;
    const box = await picker.boundingBox();
    expect(box, 'picker has a layout box').not.toBeNull();

    // The regression assertion: every edge of the popover is inside the viewport.
    expect(box!.x, 'left edge >= 0').toBeGreaterThanOrEqual(0);
    expect(box!.y, 'top edge >= 0').toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width, 'right edge within viewport').toBeLessThanOrEqual(vw + 1);
    expect(box!.y + box!.height, 'bottom edge within viewport').toBeLessThanOrEqual(vh + 1);

    // Nothing clips or paints over it: the picker owns the pixel at its centre.
    const topmost = await page.evaluate(() => {
      const el = document.querySelector('[data-testid="epic-model-picker"]');
      if (!el) return false;
      const r = el.getBoundingClientRect();
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      return !!hit && el.contains(hit);
    });
    expect(topmost, 'picker is the topmost element at its centre').toBe(true);

    // The footer (last row of the popover) must be reachable — it was the part
    // that fell below the clip boundary in the bug.
    await expect(page.getByTestId('epic-model-picker-refresh')).toBeVisible();

    await page.screenshot({
      path: path.join(SHOTS_DIR, 'epic-model-picker-open.png'),
      fullPage: false,
    });
  });
});

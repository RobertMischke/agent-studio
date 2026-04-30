import { test, Page } from '@playwright/test';
import path from 'node:path';
import { api } from './helpers/api';
import { createJob, listJobs } from './helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

const RESULTS_DIR = 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/3-progress/reihenfolge-der-tasks-bearbeiten/results';

async function clearLaneSortStorage(page: Page): Promise<void> {
  await page.addInitScript(() => {
    try { localStorage.removeItem('laneSortMode'); } catch { /* ignore */ }
  });
}

test.describe('Lane reorder — screenshots', () => {
  test('captures Custom mode with drop zones, Date mode hiding them, and a post-drag state', async ({ page }) => {
    const wps = await api<WatchPath[]>('/api/watch-paths');
    if (!wps.length) throw new Error('No watch paths');
    const watchPath = wps[0].path;
    const PREFIX = 'e2e-shot-reorder-';

    // Cleanup
    const stale = (await listJobs()).filter(j => j.watchPath === watchPath && j.id.startsWith(PREFIX));
    await Promise.all(stale.map(j =>
      fetch(`http://localhost:5030/api/jobs/${encodeURIComponent(j.id)}?watchPath=${encodeURIComponent(j.watchPath)}`, { method: 'DELETE' })
    ));

    const titleA = `${PREFIX}A — high priority`;
    const titleB = `${PREFIX}B — backlog item`;
    const titleC = `${PREFIX}C — nice to have`;
    const a = await createJob({ id: `${PREFIX}A`, title: titleA, watchPath, targetState: '2-ready' });
    const b = await createJob({ id: `${PREFIX}B`, title: titleB, watchPath, targetState: '2-ready' });
    const c = await createJob({ id: `${PREFIX}C`, title: titleC, watchPath, targetState: '2-ready' });

    const others = (await listJobs())
      .filter(j => j.state === '2-ready' && ![a.id, b.id, c.id].includes(j.id))
      .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
    await api('/api/jobs/reorder', {
      method: 'POST',
      body: JSON.stringify({ jobs: [...others, { jobId: a.id, watchPath }, { jobId: b.id, watchPath }, { jobId: c.id, watchPath }] })
    });

    try {
      await clearLaneSortStorage(page);
      await page.goto('/');
      await page.getByTestId('lane-sort-toggle').waitFor({ state: 'visible', timeout: 10_000 });

      // Screenshot 1 — Custom mode (drop zones present, drag-reorder enabled).
      await page.screenshot({ path: path.join(RESULTS_DIR, '01-custom-mode-with-drop-zones.png'), fullPage: false });

      // Screenshot 2 — Date mode (drop zones hidden, drag-reorder disabled).
      await page.getByTestId('lane-sort-toggle').click();
      await page.getByTestId('lane-sort-toggle').waitFor({ state: 'visible' });
      await page.waitForFunction(() => {
        const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
        const ready = headings.find(el => el.textContent?.trim() === 'Ready')?.closest('.column');
        return ready && ready.querySelectorAll('.column__drop-zone').length === 0;
      });
      await page.screenshot({ path: path.join(RESULTS_DIR, '02-date-mode-no-drop-zones.png'), fullPage: false });

      // Switch back to Custom and perform a drag of A to the end, then screenshot the new order.
      await page.getByTestId('lane-sort-toggle').click();
      await page.waitForFunction(() => {
        const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
        const ready = headings.find(el => el.textContent?.trim() === 'Ready')?.closest('.column');
        return ready && ready.querySelectorAll('.column__drop-zone').length > 0;
      });

      await page.evaluate(({ titleA }) => {
        const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
        const heading = headings.find(el => el.textContent?.trim() === 'Ready');
        const column = heading?.closest('.column') as HTMLElement;
        const cards = Array.from(column.querySelectorAll('app-job-card')) as HTMLElement[];
        const card = cards.find(c => c.querySelector('.job-card__title')?.textContent?.trim() === titleA)!;
        const dropZones = Array.from(column.querySelectorAll('.column__drop-zone')) as HTMLElement[];
        const last = dropZones[dropZones.length - 1];
        const dt = new DataTransfer();
        card.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer: dt }));
        last.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt }));
        last.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt }));
        card.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true, dataTransfer: dt }));
      }, { titleA });

      await page.waitForFunction(({ titleA }) => {
        const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
        const ready = headings.find(el => el.textContent?.trim() === 'Ready')?.closest('.column');
        if (!ready) return false;
        const titles = Array.from(ready.querySelectorAll('app-job-card .job-card__title')).map(el => el.textContent?.trim() ?? '');
        const ours = titles.filter(t => t.startsWith('e2e-shot-reorder-'));
        return ours.length === 3 && ours[ours.length - 1] === titleA;
      }, { titleA });

      await page.screenshot({ path: path.join(RESULTS_DIR, '03-after-drag-A-moved-to-end.png'), fullPage: false });

      // Screenshot 4 — active drop-zone state. Dispatch dragstart + dragover
      // (without drop) to leave the indicator highlighted, then snap.
      await page.evaluate(({ titleA }) => {
        const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
        const heading = headings.find(el => el.textContent?.trim() === 'Ready');
        const column = heading?.closest('.column') as HTMLElement;
        const cards = Array.from(column.querySelectorAll('app-job-card')) as HTMLElement[];
        const card = cards.find(c => c.querySelector('.job-card__title')?.textContent?.trim() === titleA)!;
        const zones = Array.from(column.querySelectorAll('.column__drop-zone')) as HTMLElement[];
        const zone = zones[1]; // a zone between two cards
        const dt = new DataTransfer();
        card.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer: dt }));
        zone.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt }));
      }, { titleA });
      await page.screenshot({ path: path.join(RESULTS_DIR, '04-active-drop-zone-indicator.png'), fullPage: false });
    } finally {
      for (const id of [a.id, b.id, c.id]) {
        await fetch(`http://localhost:5030/api/jobs/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' }).catch(() => {});
      }
    }
  });
});

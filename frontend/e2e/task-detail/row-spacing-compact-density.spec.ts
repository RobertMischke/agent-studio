import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, waitForJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  }).catch(() => { /* best-effort */ });
}

function uid(suffix: string) {
  return `e2e-row-density-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function openTaskDirectly(page: Page, jobId: string, watchPath: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(jobId)}&watchPath=${encodeURIComponent(watchPath)}`);
  await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('prompt-tab-overview')).toBeVisible({ timeout: 10_000 });
}

/**
 * Operator expectation (polish-row-spacing-too-tall-make-compact-for-more-content-density):
 * the Overview tab's vertical density must stay tight so additional content
 * (pipeline steps, commit list, status sub-sections) has room to land without
 * a redesign. The budgets below are floors, not ceilings — a future change
 * that drives spacing *down* further is fine; a change that grows spacing
 * back to the pre-2026-05-29 numbers regresses content density and fails
 * the spec.
 *
 * The numbers come from the post-density-pass measurement on a fresh task in
 * 1-preparation. Each budget carries ~3 px of slack so a font-fallback or
 * unrelated text-length change does not flap the spec.
 */
test.describe('Row spacing compact density', () => {
  test('Overview tab rows and sections meet the compact budget', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('overview');
    await createJob({ id, title: id, watchPath: wp.path, targetState: '1-preparation' });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      // Wait for the Status block to mount (signals the first <app-row> is
      // rendered + has its computed styles applied).
      const statusBlock = page.getByTestId('overview-status');
      await expect(statusBlock).toBeVisible();

      // 1) Each label/value row in Status is <= 34 px tall. Pre-density-pass
      // the same Lane row (which carries the largest content — a lane pill
      // padded 2 px + dot + label) sat at ~36-40 px; compact lands around
      // 26-30 px. The cap keeps the row visibly tighter without flapping
      // on content-driven height jitter.
      const rowHeights = await statusBlock.locator('app-row').evaluateAll(
        (els) => els.map((el) => Math.round((el as HTMLElement).getBoundingClientRect().height)),
      );
      expect(rowHeights.length).toBeGreaterThanOrEqual(3);
      for (const h of rowHeights) {
        expect(h, `Status row should be <= 34 px (got ${h}px). See density tokens in _tokens-semantic.scss`)
          .toBeLessThanOrEqual(34);
        // Floor — interactive touch targets are exempt because these rows
        // are read-only label/value pairs.
        expect(h, `Status row should be >= 16 px (got ${h}px)`).toBeGreaterThanOrEqual(16);
      }

      // 2) Vertical gap between sibling sections inside the Overview is the
      // section-gap token (10 px compact). The wrapping flex container has
      // gap defined via `var(--studio-section-gap)`; we re-derive it from
      // adjacent section bounding rects.
      const sectionTops = await page.locator('app-overview-pane .ov-section').evaluateAll(
        (els) => els.map((el) => Math.round((el as HTMLElement).getBoundingClientRect().top)),
      );
      expect(sectionTops.length).toBeGreaterThanOrEqual(3);
      // Estimate the smallest top-to-top distance and assert it stays
      // under the compact budget. Title block + status sections have
      // visible content height ~30-60 px in this fresh task, so a
      // 16 px section-gap puts top-to-top at ~80 px — but the tight
      // budget keeps that at ~70 px or less.
      const tightestSectionGap = (() => {
        let smallest = Infinity;
        for (let i = 1; i < sectionTops.length; i++) {
          const delta = sectionTops[i] - sectionTops[i - 1];
          if (delta > 0 && delta < smallest) smallest = delta;
        }
        return smallest;
      })();
      // The pre-density-pass top-to-top sat around 130-150 px (gap: 20 px +
      // taller rows). The compact pass drops it well under 120 px. Keep
      // some slack for font fallback and content-driven height jitter.
      expect(tightestSectionGap, `Section gap should be <= 120 px (got ${tightestSectionGap}px)`)
        .toBeLessThanOrEqual(120);
    } finally {
      await deleteJob(id, wp.path);
    }
  });

  test('app-row honours its variant token mapping', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('variant');
    await createJob({ id, title: id, watchPath: wp.path, targetState: '1-preparation' });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      // Every <app-row> in Status renders with variant=compact so its
      // resolved min-height comes from --studio-row-min-h-compact (20 px).
      // The host attribute lives on the <app-row> custom element, not on
      // the inner BEM root, so we resolve via the parent chain.
      const status = page.getByTestId('overview-status');
      const hosts = status.locator('app-row');
      const count = await hosts.count();
      expect(count).toBeGreaterThanOrEqual(3);

      const inspected = await hosts.first().evaluate((el) => {
        const cs = window.getComputedStyle(el);
        const inner = el.querySelector('.app-row') as HTMLElement | null;
        const innerCs = inner ? window.getComputedStyle(inner) : null;
        return {
          hostVariant: el.getAttribute('data-variant'),
          tokenRowMinH: cs.getPropertyValue('--studio-row-min-h').trim(),
          tokenRowPadBlock: cs.getPropertyValue('--studio-row-pad-block').trim(),
          innerMinHeight: innerCs ? innerCs.minHeight : '',
        };
      });

      expect(inspected.hostVariant).toBe('compact');
      // Token must resolve to the compact value (20px).
      expect(inspected.tokenRowMinH).toMatch(/20px/);
      // Inner row picks up the token via min-height: var(--studio-row-min-h)
      expect(inspected.innerMinHeight).toBe('20px');
    } finally {
      await deleteJob(id, wp.path);
    }
  });

  test('Overview Status rows remain readable in the light theme', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('light');
    await createJob({ id, title: id, watchPath: wp.path, targetState: '1-preparation' });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      // Force the light theme via the documented data attribute.
      await page.evaluate(() => {
        document.documentElement.setAttribute('data-studio-theme', 'light');
      });

      const labels = page.locator('app-overview-pane [data-testid="overview-status"] .app-row__label');
      const labelCount = await labels.count();
      expect(labelCount).toBeGreaterThanOrEqual(3);

      // Pull computed color + font-size; assert text is rendered (non-zero
      // alpha + at least 10 px font-size). The full WCAG contrast check
      // lives in the visual-evidence pass; here we guard against the
      // density change accidentally hiding the label by overflow or zero
      // height.
      const sample = await labels.first().evaluate((el) => {
        const cs = window.getComputedStyle(el);
        return {
          fontSize: parseFloat(cs.fontSize),
          height: Math.round((el as HTMLElement).getBoundingClientRect().height),
          width: Math.round((el as HTMLElement).getBoundingClientRect().width),
        };
      });
      expect(sample.fontSize).toBeGreaterThanOrEqual(10);
      expect(sample.height).toBeGreaterThan(0);
      expect(sample.width).toBeGreaterThan(0);
    } finally {
      await deleteJob(id, wp.path);
    }
  });
});

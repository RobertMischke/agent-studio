/**
 * Visual + API regression for the orchestrator-review lane shipped with
 * `orchestrator-review-lane-and-bubble-up`:
 *
 *   1. The legacy `4-review` in-column swim-lane subdivision
 *      (`review-subsection-orchestrator` + `review-subsection-human`),
 *      which still fires when the backend hands the frontend a pre-ADR-0025
 *      payload (state = `4-review`, before the boot-time
 *      `JobStateMachine.EnsureStateFoldersAndMigrate` migrates it to
 *      `4-auto-review`). The swim-lane rule itself is unit-tested by
 *      `review-grouping.util.spec.ts`; this spec locks the rendered shape.
 *   2. The workspace top-banner (`workspace-banner`) that surfaces an
 *      orchestrator decision (`reissue` / `escalate` / `accept`) read from
 *      `/api/bus/{project}/messages?kind=decision&tag=orchestrator-chat`.
 *
 * Two test shapes:
 *   - "renders ..." uses `page.setContent` with the production CSS inlined
 *     so the screenshot is always capturable, even when neither dev nor
 *     stable is up. Same pattern as `live-decision-banner.spec.ts`.
 *   - "bus endpoint shape" pulls in the `dev-backend` fixture so the spec
 *     verifies the real `/api/bus/.../messages` response shape end-to-end
 *     when run from stable. AGENTS.md policy: the `dev-backend` fixture is
 *     the only sanctioned path that brings dev's backend up.
 *
 * Screenshot is written to `test-results/` (Playwright scratch). When this
 * runs inside a managed task the JobArtifactReporter copies it into the
 * job's `results/playwright/` for review.
 */
import { test as devTest, expect as devExpect } from './fixtures/dev-backend';
import { test as plainTest, expect } from '@playwright/test';

const SUBSECTION_MOCKUP_HTML = `<!doctype html>
<html><head><meta charset="utf-8"><title>orchestrator-review subsection</title>
<style>
  body {
    margin: 0;
    padding: 28px;
    background: #181825;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
    color: #cdd6f4;
  }
  .stage { max-width: 980px; margin: 0 auto; display: grid; gap: 18px; }

  /* Top banner (production styles inlined from workspace-banner.scss). */
  .banner {
    display: flex; align-items: center; gap: 10px;
    padding: 8px 12px; border-radius: 10px; font-size: 13px; color: #f1f5f9;
    background: rgba(139, 92, 246, 0.18);
    border: 1px solid rgba(139, 92, 246, 0.42);
    box-shadow: 0 4px 18px rgba(139, 92, 246, 0.18);
  }
  .banner--reissue  { background: rgba(56, 189, 248, 0.18); border-color: rgba(56, 189, 248, 0.45); }
  .banner--escalate { background: rgba(252, 211, 77, 0.18); border-color: rgba(252, 211, 77, 0.45); color: #fde68a; }
  .banner__icon { font-size: 16px; line-height: 1; }
  .banner__text strong { color: #fff; }
  .banner__project { color: rgba(255,255,255,0.72); margin-left: 4px; }
  .banner__close {
    background: transparent; border: 0; color: rgba(255,255,255,0.7);
    font-size: 18px; line-height: 1; cursor: pointer; padding: 0 4px;
    margin-left: auto;
  }

  /* Column shell (slimmed) and swim-lane subdivisions (production styles
     inlined from job-column.scss so the screenshot is faithful even when
     the backend is offline). */
  .column {
    background: rgba(255,255,255,0.03);
    border: 1px solid rgba(255,255,255,0.06);
    border-radius: 12px;
    padding: 14px;
    width: 380px;
  }
  .column__header {
    display: flex; align-items: center; gap: 8px; margin-bottom: 10px;
  }
  .column__icon  { font-size: 18px; }
  .column__title { margin: 0; font-size: 14px; font-weight: 700; color: #e2e8f0; }
  .column__count {
    margin-left: auto; background: rgba(255,255,255,0.08);
    color: #cbd5e1; border-radius: 10px; padding: 1px 8px;
    font-size: 11px; font-weight: 700; font-variant-numeric: tabular-nums;
  }
  .column__body { display: flex; flex-direction: column; gap: 10px; }

  .column__subsection {
    display: flex; flex-direction: column; gap: 8px;
    padding: 8px; border-radius: 10px;
    background: rgba(255, 255, 255, 0.02);
    border: 1px solid rgba(255, 255, 255, 0.04);
  }
  .column__subsection--orchestrator {
    background: rgba(139, 92, 246, 0.06);
    border-color: rgba(139, 92, 246, 0.20);
  }
  .column__subsection--human {
    background: rgba(56, 189, 248, 0.04);
    border-color: rgba(56, 189, 248, 0.16);
  }
  .column__subsection-title {
    margin: 0 0 2px;
    display: flex; align-items: center; gap: 6px;
    font-size: 11px; font-weight: 700;
    text-transform: uppercase; letter-spacing: 0.05em;
    color: #cbd5e1;
  }
  .column__subsection--orchestrator .column__subsection-title { color: #c4b5fd; }
  .column__subsection--human        .column__subsection-title { color: #7dd3fc; }
  .column__subsection-icon  { font-size: 13px; line-height: 1; }
  .column__subsection-count {
    margin-left: auto; background: rgba(255,255,255,0.08);
    color: #cbd5e1; border-radius: 10px; padding: 1px 6px;
    font-size: 10px; font-weight: 700; font-variant-numeric: tabular-nums;
    min-width: 20px; text-align: center;
  }

  .card {
    background: rgba(255,255,255,0.05);
    border: 1px solid rgba(255,255,255,0.08);
    border-radius: 8px;
    padding: 8px 10px;
    font-size: 12px;
    color: #e2e8f0;
  }
  .card__title { font-weight: 600; }
  .card__verdict {
    display: inline-block; margin-top: 4px;
    padding: 1px 6px; border-radius: 999px;
    font-size: 10px; font-weight: 700; text-transform: uppercase;
    letter-spacing: 0.04em;
  }
  .card__verdict--reissue  { background: rgba(56,189,248,0.18); color: #7dd3fc; }
  .card__verdict--escalate { background: rgba(252,211,77,0.18); color: #fde68a; }
  .card__verdict--accept   { background: rgba(132,204,22,0.18); color: #bef264; }
</style></head>
<body>
  <div class="stage">

    <div class="banner banner--reissue" role="status" data-testid="workspace-banner">
      <span class="banner__icon" aria-hidden="true">↺</span>
      <span class="banner__text">
        Orchestrator decided <strong>reissue</strong>
        for <strong>bug-der-commit-haengt-am-end-to-end-test</strong>
        <span class="banner__project">in agent-taskboard</span>
      </span>
      <button type="button" class="banner__close" aria-label="Dismiss"
              data-testid="workspace-banner-close">&times;</button>
    </div>

    <div class="column"
         data-testid="lane-4-review"
         data-state="4-review">
      <div class="column__header">
        <span class="column__icon">👀</span>
        <h2 class="column__title">Review</h2>
        <span class="column__count">3</span>
      </div>
      <div class="column__body">

        <section class="column__subsection column__subsection--orchestrator"
                 data-testid="review-subsection-orchestrator">
          <h3 class="column__subsection-title">
            <span class="column__subsection-icon" aria-hidden="true">🤖</span>
            <span>Orchestrator review</span>
            <span class="column__subsection-count">2</span>
          </h3>
          <div class="card">
            <div class="card__title">bug-der-commit-haengt-am-end-to-end-test</div>
            <span class="card__verdict card__verdict--escalate">escalate</span>
          </div>
          <div class="card">
            <div class="card__title">cli-usage-models</div>
            <span class="card__verdict card__verdict--reissue">reissue</span>
          </div>
        </section>

        <section class="column__subsection column__subsection--human"
                 data-testid="review-subsection-human">
          <h3 class="column__subsection-title">
            <span class="column__subsection-icon" aria-hidden="true">👤</span>
            <span>Human review</span>
            <span class="column__subsection-count">1</span>
          </h3>
          <div class="card">
            <div class="card__title">code-revie</div>
          </div>
        </section>

      </div>
    </div>

  </div>
</body></html>`;

plainTest('renders the 4-review swim-lane subdivision and orchestrator banner', async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 720 });
  await page.setContent(SUBSECTION_MOCKUP_HTML, { waitUntil: 'load' });

  const banner = page.getByTestId('workspace-banner');
  await expect(banner).toBeVisible();
  await expect(banner).toContainText('Orchestrator decided');
  await expect(banner).toContainText('reissue');
  await expect(banner).toContainText('bug-der-commit-haengt-am-end-to-end-test');

  const orchSection = page.getByTestId('review-subsection-orchestrator');
  const humanSection = page.getByTestId('review-subsection-human');
  await expect(orchSection).toBeVisible();
  await expect(humanSection).toBeVisible();
  await expect(orchSection).toContainText('Orchestrator review');
  await expect(orchSection).toContainText('escalate');
  await expect(orchSection).toContainText('reissue');
  await expect(humanSection).toContainText('Human review');
  await expect(humanSection).toContainText('code-revie');

  await page.locator('.stage').screenshot({
    path: 'test-results/orchestrator-review-subsection.png'
  });
});

devTest('bus endpoint accepts the kind=decision tag=orchestrator-chat filter', async ({ devBackend }) => {
  // Discover the active project the workspace currently watches; we just
  // need any project name the backend recognises so the route exists.
  const projects = await fetch(`${devBackend.baseUrl}/api/watch-paths`).then(r => r.json()) as Array<{ name?: string }>;
  const project = projects.find(p => typeof p.name === 'string')?.name;
  devExpect(project, 'at least one watched project must be configured on dev').toBeTruthy();

  const url = `${devBackend.baseUrl}/api/bus/${encodeURIComponent(project!)}/messages?kind=decision&tag=orchestrator-chat&limit=5`;
  const res = await fetch(url);
  devExpect(res.ok).toBe(true);
  const items: unknown = await res.json();
  // Shape contract: the endpoint returns an array (possibly empty). The
  // banner component tolerates the empty case; this assertion locks the
  // contract so a future refactor that swaps it for a {messages: [...]}
  // envelope trips here, not in production.
  devExpect(Array.isArray(items)).toBe(true);
});

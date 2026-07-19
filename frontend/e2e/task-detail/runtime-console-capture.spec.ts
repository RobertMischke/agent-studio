import { test, expect } from '@playwright/test';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { startRuntimeCapture, type RuntimeEventLike } from '../helpers/runtime-capture';

/**
 * Locks the browser-side capture path for Product Runtime Observability.
 * Loads a small inline HTML fixture so the test does not depend on the
 * running app or backend, then asserts the helper:
 *   1. captures structured runtime events from `console.log(JSON.stringify(...))`
 *      verbatim (the producer is the source of truth);
 *   2. wraps unstructured console lines as `frontend.console` events so the
 *      raw text is preserved in the audit trail;
 *   3. records JSON-like-but-malformed lines as parse warnings in the
 *      sidecar file with the raw line preserved;
 *   4. captures uncaught page errors as `frontend.pageerror`;
 *   5. writes a JSONL file plus a `.warnings.jsonl` sidecar at the
 *      configured output path.
 */
test.describe('runtime console capture', () => {
  test('captures structured events, wraps plain logs, and records parse warnings', async ({ page }) => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'runtime-capture-spec-'));
    try {
      const capture = startRuntimeCapture(page, {
        outputDir: tmpDir,
        specSlug: 'runtime-console-capture',
        project: 'agent-taskboard',
        jobId: 'product-runtime-log-capture',
      });

      const html = `<!doctype html><meta charset="utf-8"><title>fixture</title><script>
        // 1) A structured runtime event from the built application.
        console.log(JSON.stringify({
          schemaVersion: 1,
          timestamp: '2026-05-06T12:00:00.000Z',
          level: 'Info',
          event: 'render.first-paint',
          subsystem: 'frontend',
          operation: 'bootstrap',
          duration: { ms: 142.7 },
          status: 'Ok',
          payload: { route: '/' }
        }));
        // 2) A second structured event with an error.
        console.error(JSON.stringify({
          schemaVersion: 1,
          timestamp: '2026-05-06T12:00:00.250Z',
          level: 'Error',
          event: 'http.request.failed',
          subsystem: 'frontend',
          operation: 'GET /api/tasks',
          status: 'Failed',
          error: { type: 'NetworkError', message: 'fetch failed', code: 'ERR_CONNECTION_REFUSED' }
        }));
        // 3) A plain unstructured console line.
        console.log('User clicked compose button');
        // 4) A JSON-like but malformed line.
        console.log('{not really json');
        // 5) An uncaught error.
        setTimeout(() => { throw new Error('unhandled boom'); }, 5);
      </script>`;

      await page.goto('data:text/html;charset=utf-8,' + encodeURIComponent(html));
      // Give the setTimeout-thrown error a moment to surface.
      await page.waitForTimeout(150);
      await capture.stop();

      // Files exist at the documented paths.
      expect(fs.existsSync(capture.outputPath)).toBe(true);
      expect(fs.existsSync(capture.warningsPath)).toBe(true);

      const lines = fs.readFileSync(capture.outputPath, 'utf8').trim().split('\n').filter(Boolean);
      const events = lines.map((l: string) => JSON.parse(l) as RuntimeEventLike);

      // Two structured events round-trip verbatim.
      const renderEvt = events.find(e => e.event === 'render.first-paint');
      expect(renderEvt).toBeTruthy();
      expect(renderEvt!.subsystem).toBe('frontend');
      expect(renderEvt!.operation).toBe('bootstrap');
      // Producer-emitted events are not back-filled with orchestrator metadata.
      expect((renderEvt as Record<string, unknown>).jobId ?? null).toBeNull();

      const httpEvt = events.find(e => e.event === 'http.request.failed');
      expect(httpEvt).toBeTruthy();
      expect(httpEvt!.level).toBe('Error');
      expect(httpEvt!.status).toBe('Failed');

      // Unstructured console line wrapped, and orchestrator metadata flowed through.
      const wrappedConsole = events.find(e => e.event === 'frontend.console');
      expect(wrappedConsole).toBeTruthy();
      const payload = wrappedConsole!.payload as { text: string };
      expect(payload.text).toContain('User clicked compose button');
      expect((wrappedConsole as Record<string, unknown>).project).toBe('agent-taskboard');
      expect((wrappedConsole as Record<string, unknown>).jobId).toBe('product-runtime-log-capture');

      // Uncaught page error captured.
      const pageError = events.find(e => e.event === 'frontend.pageerror');
      expect(pageError).toBeTruthy();
      const errBlock = (pageError as Record<string, unknown>).error as { message: string };
      expect(errBlock.message).toContain('unhandled boom');

      // Malformed JSON-like line surfaced in warnings sidecar with raw line preserved.
      const warningLines = fs.readFileSync(capture.warningsPath, 'utf8').trim().split('\n').filter(Boolean);
      expect(warningLines.length).toBeGreaterThanOrEqual(1);
      const warning = JSON.parse(warningLines[0]!) as { reason: string; rawLine: string };
      expect(warning.reason).toContain('json parse');
      expect(warning.rawLine).toContain('{not really json');
    } finally {
      try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch { /* best-effort */ }
    }
  });

  test('routes output under JOB_RESULTS_DIR/runtime when env var is set', async ({ page }) => {
    const jobResultsDir = fs.mkdtempSync(path.join(os.tmpdir(), 'runtime-capture-job-'));
    const previous = process.env.JOB_RESULTS_DIR;
    process.env.JOB_RESULTS_DIR = jobResultsDir;
    try {
      const capture = startRuntimeCapture(page, { specSlug: 'job-results-routing' });
      await page.goto('data:text/html;charset=utf-8,' + encodeURIComponent('<script>console.log("ok")</script>'));
      await capture.stop();

      expect(capture.outputPath.startsWith(path.resolve(jobResultsDir, 'runtime'))).toBe(true);
      expect(fs.existsSync(capture.outputPath)).toBe(true);
    } finally {
      if (previous === undefined) delete process.env.JOB_RESULTS_DIR;
      else process.env.JOB_RESULTS_DIR = previous;
      try { fs.rmSync(jobResultsDir, { recursive: true, force: true }); } catch { /* best-effort */ }
    }
  });
});

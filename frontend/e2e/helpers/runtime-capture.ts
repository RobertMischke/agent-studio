/**
 * Browser console capture for Product Runtime Observability.
 *
 * Mirrors the schema in `docs/system/schemas/product-runtime-event.schema.json` and
 * the file layout in `docs/operations/runtime/log-capture.md`. Adapter-style: the
 * built application emits structured events via whatever logging library it
 * already uses (winston-style JSON, console.log of a JSON string, plain
 * console messages). The helper sniffs each console line: if it parses as a
 * runtime event envelope, it is captured verbatim; otherwise it is wrapped in
 * a `frontend.console` event so the raw line is still preserved alongside
 * structured signals.
 *
 * Three sinks:
 *   - `console`     - any browser console line. Structured JSON is unwrapped;
 *                     plain text is wrapped as `frontend.console` with
 *                     payload.text.
 *   - `pageerror`   - uncaught exceptions inside the page. Captured as a
 *                     `frontend.pageerror` event at level Error with the
 *                     stack in error.stack.
 *   - `requestfailed` - failed network requests. Captured as a
 *                     `frontend.request.failed` event at level Warn.
 *
 * Output paths:
 *   - When `JOB_RESULTS_DIR` is set (orchestrator mode):
 *     `<JOB_RESULTS_DIR>/runtime/<spec-slug>.jsonl` plus
 *     `<JOB_RESULTS_DIR>/runtime/<spec-slug>.jsonl.warnings.jsonl`.
 *   - Otherwise (local dev): a caller-supplied directory, defaulting to
 *     `frontend/e2e/test-results/runtime/`.
 *
 * Malformed JSON-like console lines (start with `{` but fail to parse) are
 * recorded in the warnings sidecar with the raw line preserved, matching the
 * backend reader's behaviour and the task contract's "preserve raw logs and
 * expose parse warnings when events are malformed" rule.
 */

import fs from 'fs';
import path from 'path';
import type { Page, ConsoleMessage } from '@playwright/test';

export interface RuntimeCaptureOptions {
  /** Override for the output directory. Defaults to JOB_RESULTS_DIR/runtime or frontend/e2e/test-results/runtime. */
  outputDir?: string;
  /** Spec slug for the output filename. Use the test title or a stable id. */
  specSlug: string;
  /** Optional fixed metadata to attach to wrapped frontend.* events. */
  project?: string;
  jobId?: string;
  runId?: string;
}

export interface RuntimeEventLike {
  schemaVersion: number;
  timestamp: string;
  level: string;
  event: string;
  subsystem: string;
  // Open shape; all other fields flow through.
  [key: string]: unknown;
}

export interface RuntimeWarningRecord {
  sourcePath: string;
  lineNumber: number;
  reason: string;
  rawLine: string;
  recordedAt: string;
}

export interface RuntimeCapture {
  /** Stop listening and flush any buffered output. */
  stop: () => Promise<void>;
  /** Captured events in the order they were observed. */
  events: () => RuntimeEventLike[];
  /** Parse warnings for malformed JSON-like console lines. */
  warnings: () => RuntimeWarningRecord[];
  /** Absolute path to the JSONL file. */
  outputPath: string;
  /** Absolute path to the warnings sidecar. */
  warningsPath: string;
}

const VALID_LEVELS = new Set(['Trace', 'Debug', 'Info', 'Warn', 'Error', 'Fatal']);
const EVENT_NAME_RE = /^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)*$/;

function looksLikeEvent(value: unknown): value is RuntimeEventLike {
  if (!value || typeof value !== 'object') return false;
  const obj = value as Record<string, unknown>;
  if (obj.schemaVersion !== 1) return false;
  if (typeof obj.timestamp !== 'string') return false;
  if (typeof obj.level !== 'string' || !VALID_LEVELS.has(obj.level)) return false;
  if (typeof obj.event !== 'string' || !EVENT_NAME_RE.test(obj.event)) return false;
  if (typeof obj.subsystem !== 'string' || obj.subsystem.length === 0) return false;
  return true;
}

function consoleLevelToRuntimeLevel(t: string): string {
  switch (t) {
    case 'error': return 'Error';
    case 'warning': return 'Warn';
    case 'debug': return 'Debug';
    case 'trace': return 'Trace';
    default: return 'Info';
  }
}

function slugify(s: string): string {
  return s.replace(/[^a-z0-9-]+/gi, '-').replace(/^-+|-+$/g, '').toLowerCase() || 'spec';
}

function defaultOutputDir(): string {
  if (process.env.JOB_RESULTS_DIR) {
    return path.join(process.env.JOB_RESULTS_DIR, 'runtime');
  }
  return path.join('frontend', 'e2e', 'test-results', 'runtime');
}

/**
 * Attach console + pageerror + requestfailed listeners to the page and write
 * captured runtime events as JSONL into a per-spec file. Caller must invoke
 * `stop()` before the test exits so the file is flushed.
 */
export function startRuntimeCapture(page: Page, options: RuntimeCaptureOptions): RuntimeCapture {
  const baseDir = options.outputDir ?? defaultOutputDir();
  const slug = slugify(options.specSlug);
  const outputPath = path.resolve(baseDir, `${slug}.jsonl`);
  const warningsPath = `${outputPath}.warnings.jsonl`;
  fs.mkdirSync(baseDir, { recursive: true });

  const events: RuntimeEventLike[] = [];
  const warnings: RuntimeWarningRecord[] = [];
  let lineNumber = 0;

  const meta = { project: options.project, jobId: options.jobId, runId: options.runId };

  const writeEvent = (evt: RuntimeEventLike) => {
    events.push(evt);
    let line = JSON.stringify(evt);
    if (line.includes('\n')) line = line.replace(/\r?\n/g, ' ');
    fs.appendFileSync(outputPath, line + '\n', 'utf8');
  };

  const writeWarning = (rec: RuntimeWarningRecord) => {
    warnings.push(rec);
    let line = JSON.stringify(rec);
    if (line.includes('\n')) line = line.replace(/\r?\n/g, ' ');
    fs.appendFileSync(warningsPath, line + '\n', 'utf8');
  };

  const ingestText = (text: string, fallbackLevel: string) => {
    lineNumber++;
    if (!text || !text.trim()) return;
    const trimmed = text.trim();
    if (trimmed[0] === '{') {
      let parsed: unknown;
      try {
        parsed = JSON.parse(trimmed);
      } catch (err) {
        writeWarning({
          sourcePath: outputPath,
          lineNumber,
          reason: `json parse: ${(err as Error).message}`,
          rawLine: text,
          recordedAt: new Date().toISOString(),
        });
        return;
      }
      if (looksLikeEvent(parsed)) {
        // Producer-emitted runtime event: pass through verbatim. Do not
        // mutate or backfill orchestrator metadata; that would corrupt the
        // producer's own event identity.
        writeEvent(parsed);
        return;
      }
      writeWarning({
        sourcePath: outputPath,
        lineNumber,
        reason: 'json object not a runtime event (missing schemaVersion/timestamp/level/event/subsystem)',
        rawLine: text,
        recordedAt: new Date().toISOString(),
      });
      return;
    }
    // Plain console line: wrap so the audit trail is still searchable.
    writeEvent({
      schemaVersion: 1,
      timestamp: new Date().toISOString(),
      level: fallbackLevel,
      event: 'frontend.console',
      subsystem: 'frontend',
      project: meta.project,
      jobId: meta.jobId,
      runId: meta.runId,
      payload: { text },
    });
  };

  const onConsole = (msg: ConsoleMessage) => {
    ingestText(msg.text(), consoleLevelToRuntimeLevel(msg.type()));
  };
  const onPageError = (err: Error) => {
    lineNumber++;
    writeEvent({
      schemaVersion: 1,
      timestamp: new Date().toISOString(),
      level: 'Error',
      event: 'frontend.pageerror',
      subsystem: 'frontend',
      project: meta.project,
      jobId: meta.jobId,
      runId: meta.runId,
      status: 'Failed',
      error: {
        type: err.name,
        message: err.message,
        stack: err.stack?.slice(0, 8000),
      },
    });
  };
  const onRequestFailed = (req: { url(): string; failure(): { errorText: string } | null; method(): string }) => {
    lineNumber++;
    writeEvent({
      schemaVersion: 1,
      timestamp: new Date().toISOString(),
      level: 'Warn',
      event: 'frontend.request.failed',
      subsystem: 'frontend',
      project: meta.project,
      jobId: meta.jobId,
      runId: meta.runId,
      status: 'Failed',
      payload: { url: req.url(), method: req.method(), error: req.failure()?.errorText ?? null },
    });
  };

  page.on('console', onConsole);
  page.on('pageerror', onPageError);
  page.on('requestfailed', onRequestFailed);

  return {
    stop: async () => {
      page.off('console', onConsole);
      page.off('pageerror', onPageError);
      page.off('requestfailed', onRequestFailed);
    },
    events: () => events.slice(),
    warnings: () => warnings.slice(),
    outputPath,
    warningsPath,
  };
}

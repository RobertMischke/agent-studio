import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import {
  rebaseReportLinks,
  renderReport,
  validateRunResult
} from '../report.mjs';

const suiteRoot = path.resolve(import.meta.dirname, '..');
const validFixture = path.join(suiteRoot, 'fixtures', 'report', 'validated-runs.json');
const malformedFixture = path.join(suiteRoot, 'fixtures', 'report', 'malformed-runs.json');

test('validated fixture satisfies schema and cross-field invariants', async () => {
  const report = JSON.parse(await readFile(validFixture, 'utf8'));
  assert.deepEqual(validateRunResult(report), { valid: true, errors: [] });
});

test('renderer exposes summary, timeline, telemetry gaps, incidents, and raw evidence', async () => {
  const report = JSON.parse(await readFile(validFixture, 'utf8'));
  const html = renderReport(report);
  assert.match(html, /Acceptance matrix/);
  assert.match(html, /Healed/);
  assert.match(html, /Recovered/);
  assert.match(html, /Lost/);
  assert.match(html, /Queue versus execution/);
  assert.match(html, /Phase timeline/);
  assert.match(html, /Token telemetry/);
  assert.match(html, />Unavailable</);
  assert.match(html, /Injected incidents and recovery/);
  assert.match(html, /historie\.html#incident-zombie-leases/);
  assert.match(html, /id="assertion-gate-loss-003-gate-result-recovered"/);
  assert.match(html, /raw\/gate-loss-003\.json#gate-replay/);
  assert.doesNotMatch(html, /leaderboard|grouped by model|ranked by (?:model|CLI)/i);
});

test('invalid report is rejected without silently dropping malformed runs', async () => {
  const report = JSON.parse(await readFile(malformedFixture, 'utf8'));
  const validation = validateRunResult(report);
  assert.equal(validation.valid, false);
  assert.ok(validation.errors.length >= 8);
  const html = renderReport(null, { validationErrors: validation.errors });
  assert.match(html, /Input rejected/);
  assert.match(html, /No run was silently omitted/);
  assert.match(html, /\$\.runs\[0\]\.taskKey/);
});

test('accepted runs cannot contain failed assertions and token attribution cannot exceed totals', async () => {
  const report = JSON.parse(await readFile(validFixture, 'utf8'));
  report.runs[0].assertions[0].status = 'fail';
  report.runs[0].tokens.phases.run = report.runs[0].tokens.total + 1;
  const validation = validateRunResult(report);
  assert.match(validation.errors.join('\n'), /accepted: cannot be true/);
  assert.match(validation.errors.join('\n'), /attribution exceeds/);
});

test('accepted fail-closed scenarios may report expected non-delivery without inventing a Result SHA', async () => {
  const report = JSON.parse(await readFile(validFixture, 'utf8'));
  report.runs[0].resultSha = null;
  const validation = validateRunResult(report);
  assert.deepEqual(validation, { valid: true, errors: [] });
  assert.match(renderReport(report), /Not published \(expected non-delivery\)/);
});

test('relative artifact and chronicle links are rebased for a repository report', async () => {
  const report = JSON.parse(await readFile(validFixture, 'utf8'));
  const output = path.resolve(suiteRoot, '..', '..', 'docs', 'quality', 'remote-run-testsuite-report', 'index.html');
  const rebased = rebaseReportLinks(report, validFixture, output);
  assert.equal(rebased.runs[0].scenario.manifestHref, '../../../tools/remote-test-suite/scenarios/reference-change.json');
  assert.equal(rebased.runs[2].rawArtifactHref, '../../../tools/remote-test-suite/fixtures/report/raw/gate-loss-003.json');
  assert.equal(rebased.suite.chronicleHref, '../../operations/haertung-verteilte-ausfuehrung/historie.html');
});

test('CLI writes a visible rejection report and exits nonzero for schema-incompatible input', async () => {
  const temporary = await mkdtemp(path.join(os.tmpdir(), 'remote-report-'));
  const output = path.join(temporary, 'rejected.html');
  const result = spawnSync(process.execPath, [
    path.join(suiteRoot, 'report.mjs'),
    '--input', malformedFixture,
    '--output', output
  ], { encoding: 'utf8' });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /Visible report written/);
  assert.match(await readFile(output, 'utf8'), /Input rejected/);
});

test('CLI writes a visible parse error report for malformed JSON', async () => {
  const temporary = await mkdtemp(path.join(os.tmpdir(), 'remote-report-json-'));
  const input = path.join(temporary, 'malformed.json');
  const output = path.join(temporary, 'rejected.html');
  await writeFile(input, '{"schemaVersion": 1, broken');
  const result = spawnSync(process.execPath, [
    path.join(suiteRoot, 'report.mjs'),
    '--input', input,
    '--output', output
  ], { encoding: 'utf8' });
  assert.equal(result.status, 2);
  assert.match(await readFile(output, 'utf8'), /JSON parse failed/);
  assert.match(await readFile(output, 'utf8'), /No run was silently omitted/);
});

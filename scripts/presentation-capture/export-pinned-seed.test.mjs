import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import test from 'node:test';

import { buildSnapshot, writeSnapshot } from './export-pinned-seed.mjs';

function writeTask(root, project, key, state, title, withReview = false) {
  const dir = join(root, 'projects', project, 'tasks', '000', key);
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, 'task.json'), `${JSON.stringify({ key, state, title, taskType: 'feature' })}\n`);
  writeFileSync(join(dir, 'prompt.md'), `# ${title}\n\nInspect C:/Users/Private/${project}/${key}.\n`);
  if (withReview) {
    writeFileSync(join(dir, 'code-review-grade-2026.md'), [
      '---',
      'grade: B',
      `summary: Review ${key} in ${project}.`,
      '---',
      '',
      `- Add coverage for ${key}.`,
    ].join('\n'));
  }
}

test('export is deterministic and removes source names, keys, and paths', () => {
  const root = mkdtempSync(join(tmpdir(), 'pinned-export-'));
  try {
    writeTask(root, 'private-product', 'SECRET-4', '0-backlog', 'Plan SECRET-4 for Private Product');
    writeTask(root, 'private-product', 'SECRET-41', '2-ready', 'Prepare SECRET-41 for Private Product');
    writeTask(root, 'private-product', 'SECRET-42', '5-human-review', 'Approve SECRET-42 for Private Product', true);
    const args = { sourceRoot: root, project: 'private-product', task: 'SECRET-42' };
    const first = buildSnapshot(args);
    const second = buildSnapshot(args);
    assert.deepEqual(second, first);

    const outputA = join(root, 'a.json');
    const outputB = join(root, 'b.json');
    writeSnapshot(outputA, first);
    writeSnapshot(outputB, second);
    assert.equal(readFileSync(outputA, 'utf8'), readFileSync(outputB, 'utf8'));

    const serialized = JSON.stringify(first);
    assert.doesNotMatch(serialized, /SECRET-4(?:1|2)?|private-product|Private Product|C:\/Users\/Private/);
    assert.match(serialized, /DEMO-9/);
    assert.match(first.decision.promptMarkdown, /^# Approve DEMO-9 for Demo App/m);
    assert.equal(first.decision.reviewSummary, 'Review DEMO-9 in Demo App.');
    assert.equal(first.fixedTimeBase, '2026-08-09T08:00:00.000Z');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

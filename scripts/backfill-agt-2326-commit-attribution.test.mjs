import assert from 'node:assert/strict';
import test from 'node:test';

import {
  BACKFILL,
  runBackfill,
} from './backfill-agt-2326-commit-attribution.mjs';

const EXPECTED = [
  ['AGT-2298', '2a25bd3a46cde65a1dbe9e2f357b14c029bfd1b9'],
  ['AGT-2300', 'ff982d2981209fa92726037053e566f5045ce643'],
  ['AGT-2320', '848c11acb50cf7311d3604ac3e1e0755f6155ed1'],
  ['AGT-2321', 'e36ee91e6ca9909a7fbe5686fdfffdacb7d52f58'],
];

function jsonResponse(status, value) {
  const body = JSON.stringify(value);
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => value,
    text: async () => body,
  };
}

test('manifest contains exactly one requested develop integration SHA per source card', () => {
  assert.deepEqual(BACKFILL.map(item => [item.taskId, item.sha]), EXPECTED);
  assert.equal(new Set(BACKFILL.map(item => item.taskId)).size, 4);
  assert.equal(new Set(BACKFILL.map(item => item.sha)).size, 4);
});

test('dry run performs no HTTP requests', async () => {
  let requests = 0;
  const result = await runBackfill(
    { apply: false },
    {
      fetchImpl: async () => {
        requests++;
        throw new Error('unexpected request');
      },
      write: () => {},
    },
  );

  assert.equal(requests, 0);
  assert.deepEqual(result, { applied: 0, failed: 0, dryRun: true });
});

test('apply resolves the canonical watchPath and replaces each chain with one SHA', async () => {
  const calls = [];
  const fetchImpl = async (url, init = {}) => {
    calls.push({ url, init });
    if (url.endsWith('/api/watch-paths')) {
      return jsonResponse(200, [
        { name: 'Other Project', path: '/task-store/other' },
        { name: 'Agent Task Processor', path: '/task-store/canonical' },
      ]);
    }
    return jsonResponse(200, { commits: [] });
  };

  const result = await runBackfill(
    {
      apply: true,
      baseUrl: 'http://127.0.0.1:5031',
      project: 'Agent Task Processor',
      clientId: 'operator-phase-2',
    },
    { fetchImpl, write: () => {} },
  );

  assert.equal(result.applied, 4);
  assert.equal(result.failed, 0);
  assert.equal(calls.length, 5);
  assert.equal(calls[0].init.headers['X-Client-Id'], 'operator-phase-2');

  for (let index = 0; index < EXPECTED.length; index++) {
    const [taskId, sha] = EXPECTED[index];
    const call = calls[index + 1];
    assert.match(
      call.url,
      new RegExp(`/api/tasks/${taskId}/commits\\?watchPath=%2Ftask-store%2Fcanonical$`),
    );
    assert.equal(call.init.method, 'PUT');
    assert.equal(call.init.headers['X-Client-Id'], 'operator-phase-2');
    assert.deepEqual(JSON.parse(call.init.body), { commits: [sha] });
  }
});

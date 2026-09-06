import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

test('fixture has the expected release state', () => {
  assert.equal(readFileSync('release-state.txt', 'utf8').trim(), 'pass');
});

import assert from 'node:assert/strict';
import test from 'node:test';
import { deploymentReady } from './scenario.mjs';

test('the seeded passing test is stable', () => {
  assert.equal(2 + 2, 4);
});

test('the fake CLI makes the deployment fixture pass', () => {
  assert.equal(deploymentReady(), true);
});

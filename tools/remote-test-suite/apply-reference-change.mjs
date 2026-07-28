import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

const seedIndex = process.argv.indexOf('--seed');
const seed = seedIndex >= 0 ? process.argv[seedIndex + 1] : '';
if (!/^[A-Za-z0-9._-]{1,80}$/.test(seed)) {
  throw new Error('A safe --seed value is required.');
}

await mkdir('src', { recursive: true });
await mkdir('test', { recursive: true });

await writeFile('package.json', `${JSON.stringify({
  name: 'shipping-reference-fixture',
  private: true,
  type: 'module',
  scripts: { test: 'node --test test/*.test.mjs' }
}, null, 2)}\n`);

await writeFile('src/shipping.mjs', `const ZONES = Object.freeze({
  local: Object.freeze({ base: 4.25, perItem: 0.8 }),
  regional: Object.freeze({ base: 6.5, perItem: 1.15 }),
  international: Object.freeze({ base: 13.75, perItem: 2.4 })
});

export function priorityShippingQuote({ zone, items }) {
  const rate = ZONES[zone];
  if (!rate) throw new RangeError(\`Unknown destination zone: \${zone}\`);
  if (!Number.isInteger(items) || items < 1) {
    throw new RangeError('items must be a positive integer');
  }
  return Math.round((rate.base + rate.perItem * items) * 100) / 100;
}

export const shippingFixtureSeed = '${seed}';
`);

await writeFile('src/index.mjs', `export {
  priorityShippingQuote,
  shippingFixtureSeed
} from './shipping.mjs';
`);

await writeFile('test/shipping.test.mjs', `import test from 'node:test';
import assert from 'node:assert/strict';
import { priorityShippingQuote, shippingFixtureSeed } from '../src/index.mjs';

test('quotes every declared zone deterministically', () => {
  assert.equal(priorityShippingQuote({ zone: 'local', items: 3 }), 6.65);
  assert.equal(priorityShippingQuote({ zone: 'regional', items: 2 }), 8.8);
  assert.equal(priorityShippingQuote({ zone: 'international', items: 4 }), 23.35);
});

test('rejects invalid semantic inputs', () => {
  assert.throws(() => priorityShippingQuote({ zone: 'moon', items: 1 }), RangeError);
  assert.throws(() => priorityShippingQuote({ zone: 'local', items: 0 }), RangeError);
  assert.throws(() => priorityShippingQuote({ zone: 'local', items: 1.5 }), RangeError);
});

test('records the stable scenario seed as telemetry input', () => {
  assert.equal(shippingFixtureSeed, '${seed}');
});
`);

await writeFile('README.md', `# Shipping reference fixture

This isolated repository implements deterministic priority shipping quotes.
Its scenario seed is \`${seed}\`; the seed affects fixture identity, not the
acceptance rules.
`);

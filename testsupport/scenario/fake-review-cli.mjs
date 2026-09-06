import { appendFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';

const [repository, evidence] = process.argv.slice(2);
if (!repository || !evidence) {
  console.error('Usage: fake-review-cli.mjs <repository> <evidence-log>');
  process.exit(2);
}
const result = spawnSync(process.execPath, ['--test', 'scenario.test.mjs'], {
  cwd: repository,
  encoding: 'utf8'
});
await appendFile(evidence, result.stdout + result.stderr, 'utf8');
if (result.status !== 0) process.exit(result.status ?? 1);
console.log('{"outcome":"Pass","summary":"fixed review output"}');

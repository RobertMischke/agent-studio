import { appendFile, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';

const [repository, evidence] = process.argv.slice(2);
if (!repository || !evidence) {
  console.error('Usage: fake-coding-cli.mjs <repository> <evidence-log>');
  process.exit(2);
}

await writeFile(
  path.join(repository, 'scenario.mjs'),
  'export function deploymentReady() {\n  return true;\n}\n',
  'utf8');
const git = (...args) => spawnSync('git', args, {
  cwd: repository,
  encoding: 'utf8',
  env: {
    ...process.env,
    GIT_AUTHOR_DATE: '2026-09-06T12:00:04Z',
    GIT_COMMITTER_DATE: '2026-09-06T12:00:04Z'
  }
});
let result = git('add', 'scenario.mjs');
if (result.status === 0) {
  result = git('-c', 'user.name=Deployment Scenario', '-c',
    'user.email=scenario@example.invalid', 'commit', '-m',
    'test: make deployment fixture pass');
}
if (result.status !== 0) {
  console.error(result.stderr || result.stdout);
  process.exit(result.status ?? 1);
}
await appendFile(evidence, 'fake-cli-output=fixed\nfake-cli-commit=created\n', 'utf8');
console.log('{"type":"agent_message","text":"deterministic deployment fixture fixed"}');
console.log('[[TASK_DONE]]');

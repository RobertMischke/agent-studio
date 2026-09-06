import assert from 'node:assert/strict';
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const definitionPath = path.join(repositoryRoot, 'testsupport', 'scenario', 'deployment-scenario.json');

test('the scenario definition has one ordered six-step smoke prefix', async () => {
  const definition = JSON.parse(await readFile(definitionPath, 'utf8'));
  assert.equal(definition.schemaVersion, 1);
  assert.equal(definition.smokeBudgetSeconds, 180);
  assert.equal(definition.steps.length, 13);
  assert.deepEqual(definition.steps.slice(0, 6).map(step => step.level), Array(6).fill('smoke'));
  assert.equal(new Set(definition.steps.map(step => step.id)).size, definition.steps.length);
  for (const step of definition.steps) {
    assert.match(step.id, /^[a-z0-9-]+$/);
    assert.ok(step.assertion.type);
    assert.notEqual(step.assertion.expected, undefined);
    assert.equal(step.evidence, `${step.id}.json`);
  }
});

test('the fixed fake CLIs make the same red repository green with the same commit', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'deployment-scenario-contract-'));
  const childEnvironment = { ...process.env };
  delete childEnvironment.NODE_TEST_CONTEXT;
  try {
    const shas = [];
    for (const name of ['first', 'second']) {
      const repository = path.join(root, name);
      const log = path.join(root, `${name}.log`);
      await cp(path.join(repositoryRoot, 'testsupport', 'scenario', 'fixture-repository'), repository, { recursive: true });
      let result = spawnSync('git', ['init', '-b', 'main'], { cwd: repository, encoding: 'utf8' });
      assert.equal(result.status, 0, result.stderr);
      result = spawnSync('git', ['add', '.'], { cwd: repository, encoding: 'utf8' });
      assert.equal(result.status, 0, result.stderr);
      result = spawnSync('git', ['-c', 'user.name=Deployment Scenario', '-c', 'user.email=scenario@example.invalid',
        'commit', '-m', 'test: seed deployment scenario'], {
        cwd: repository, encoding: 'utf8',
        env: { ...process.env, GIT_AUTHOR_DATE: '2026-09-06T12:00:00Z', GIT_COMMITTER_DATE: '2026-09-06T12:00:00Z' }
      });
      assert.equal(result.status, 0, result.stderr);
      assert.notEqual(spawnSync(process.execPath, ['--test', 'scenario.test.mjs'], { cwd: repository, env: childEnvironment }).status, 0);
      await writeFile(log, '', 'utf8');
      result = spawnSync(process.execPath, [path.join(repositoryRoot, 'testsupport', 'scenario', 'fake-coding-cli.mjs'), repository, log], { cwd: repository, encoding: 'utf8', env: childEnvironment });
      assert.equal(result.status, 0, result.stderr);
      result = spawnSync(process.execPath, [path.join(repositoryRoot, 'testsupport', 'scenario', 'fake-review-cli.mjs'), repository, log], { cwd: repository, encoding: 'utf8', env: childEnvironment });
      assert.equal(result.status, 0, result.stderr);
      shas.push(spawnSync('git', ['rev-parse', 'HEAD'], { cwd: repository, encoding: 'utf8' }).stdout.trim());
    }
    assert.equal(shas[0], shas[1]);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('the shell runner reserves exit code 2 for invalid configuration', () => {
  const result = spawnSync('bash', [path.join(repositoryRoot, 'scripts', 'scenario.sh'), '--target', 'invalid', '--level', 'smoke'], { encoding: 'utf8' });
  assert.equal(result.status, 2);
});

test('distributed container entry points use the published assembly names', async () => {
  const expected = new Map([
    ['task-server/Dockerfile', 'ENTRYPOINT ["dotnet", "task-server.dll"]'],
    ['studio-bff/Dockerfile', 'ENTRYPOINT ["dotnet", "agent-studio-bff.dll"]'],
    ['orchestrator-engine/Dockerfile', 'ENTRYPOINT ["dotnet", "orchestrator-engine.dll"]'],
    ['testsupport/scenario/Dockerfile', 'ENTRYPOINT ["dotnet", "/opt/agent-host/agent-host.dll", "--poll"]']
  ]);
  for (const [file, line] of expected) {
    const content = await readFile(path.join(repositoryRoot, file), 'utf8');
    assert.match(content, new RegExp(line.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
  }
});

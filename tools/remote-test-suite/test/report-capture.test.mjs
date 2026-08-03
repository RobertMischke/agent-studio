import test from 'node:test';
import assert from 'node:assert/strict';
import {
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  writeFile
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {
  copyReportEvidenceTree,
  isSensitiveEnvironmentName,
  sanitizeEnvironmentValues
} from '../report-capture.mjs';

const repoRoot = path.resolve(import.meta.dirname, '..', '..', '..');

test('Docker inspect capture redacts sensitive environment values by name', () => {
  const source = JSON.stringify([{
    Config: {
      Env: [
        'PATH=/usr/local/bin',
        'SERVICE_TOKEN=token-value',
        'CLIENT_SECRET=secret-value',
        'SSH_PRIVATE_KEY=key-value',
        'GITHUB_PAT=pat-value',
        'COMPAT_MODE=enabled'
      ]
    }
  }]);

  const captured = JSON.parse(sanitizeEnvironmentValues(source));
  assert.deepEqual(captured[0].Config.Env, [
    'PATH=/usr/local/bin',
    'SERVICE_TOKEN=[REDACTED]',
    'CLIENT_SECRET=[REDACTED]',
    'SSH_PRIVATE_KEY=[REDACTED]',
    'GITHUB_PAT=[REDACTED]',
    'COMPAT_MODE=enabled'
  ]);
});

test('capture also removes known credentials and handles non-JSON diagnostics', () => {
  const knownCredential = 'disposable-auth-value';
  const diagnostic = [
    `Authorization: Bearer ${knownCredential}`,
    'SERVICE_TOKEN=unknown-token',
    'PATH=/usr/bin'
  ].join('\n');
  const captured = sanitizeEnvironmentValues(diagnostic, [knownCredential]);
  assert.doesNotMatch(captured, /disposable-auth-value|unknown-token/);
  assert.match(captured, /SERVICE_TOKEN=\[REDACTED\]/);
  assert.match(captured, /PATH=\/usr\/bin/);
});

test('report evidence is sanitized before the destination file is written', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'report-capture-'));
  const source = path.join(root, 'source');
  const destination = path.join(root, 'destination');
  await mkdir(path.join(source, 'inspect'), { recursive: true });
  await writeFile(
    path.join(source, 'inspect', 'runner.json'),
    JSON.stringify([{ Config: { Env: ['RUNNER_TOKEN=not-safe', 'PATH=/bin'] } }]));

  await copyReportEvidenceTree(source, destination);

  const published = await readFile(
    path.join(destination, 'inspect', 'runner.json'), 'utf8');
  assert.doesNotMatch(published, /not-safe/);
  assert.match(published, /RUNNER_TOKEN=\[REDACTED\]/);
  assert.match(
    await readFile(path.join(source, 'inspect', 'runner.json'), 'utf8'),
    /not-safe/);
});

test('checked-in Docker inspect evidence contains no exposed sensitive environment values', async () => {
  const reportRoot = path.join(
    repoRoot, 'docs', 'quality', 'remote-run-testsuite-report');
  const inspectFiles = await findInspectJson(reportRoot);
  assert.ok(inspectFiles.length > 0, 'Expected checked-in Docker inspect evidence.');

  for (const file of inspectFiles) {
    const document = JSON.parse(await readFile(file, 'utf8'));
    for (const assignment of collectEnvironmentAssignments(document)) {
      const separator = assignment.indexOf('=');
      if (separator < 0) continue;
      const name = assignment.slice(0, separator);
      if (isSensitiveEnvironmentName(name)) {
        assert.equal(
          assignment.slice(separator + 1),
          '[REDACTED]',
          `${path.relative(repoRoot, file)} exposes ${name}`);
      }
    }
  }
});

test('all seven product Dockerfiles exclude local credentials from their build contexts', async () => {
  const rootContextDockerfiles = [
    'backend/Dockerfile',
    'frontend/Dockerfile',
    'orchestrator-engine/Dockerfile',
    'runner/Dockerfile',
    'studio-bff/Dockerfile',
    'task-server/Dockerfile'
  ];
  const relayDockerfile = 'companion/relay/Dockerfile';
  assert.equal(rootContextDockerfiles.length + 1, 7);
  await Promise.all([...rootContextDockerfiles, relayDockerfile]
    .map(file => readFile(path.join(repoRoot, file), 'utf8')));

  await assertCredentialIgnorePolicy(path.join(repoRoot, '.dockerignore'));
  await assertCredentialIgnorePolicy(
    path.join(repoRoot, 'companion', 'relay', '.dockerignore'));
});

async function assertCredentialIgnorePolicy(file) {
  const dockerignore = (await readFile(file, 'utf8')).split(/\r?\n/);
  for (const pattern of ['**/*.env', '**/*.token', '**/.git-credentials']) {
    assert.ok(
      dockerignore.includes(pattern),
      `${path.relative(repoRoot, file)} is missing ${pattern}`);
  }
  for (const pattern of [
    '!**/*.env.template',
    '!**/*.env.example',
    '!**/*.token.template',
    '!**/*.token.example',
    '!**/.git-credentials.template',
    '!**/.git-credentials.example'
  ]) {
    assert.ok(
      dockerignore.includes(pattern),
      `${path.relative(repoRoot, file)} is missing ${pattern}`);
  }
}

async function findInspectJson(root) {
  const found = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const target = path.join(root, entry.name);
    if (entry.isDirectory()) {
      found.push(...await findInspectJson(target));
    } else if (entry.isFile()
        && entry.name.endsWith('.json')
        && path.basename(path.dirname(target)) === 'inspect') {
      found.push(target);
    }
  }
  return found;
}

function collectEnvironmentAssignments(value, found = []) {
  if (!value || typeof value !== 'object') return found;
  if (Array.isArray(value)) {
    for (const item of value) collectEnvironmentAssignments(item, found);
    return found;
  }
  for (const [name, child] of Object.entries(value)) {
    if (name.toLowerCase() === 'env' && Array.isArray(child)) {
      found.push(...child.filter(item => typeof item === 'string'));
    } else {
      collectEnvironmentAssignments(child, found);
    }
  }
  return found;
}

#!/usr/bin/env node
import { createServer } from 'node:net';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import {
  appendJsonl,
  cleanupRunRoot,
  expectedPhases,
  interpolate,
  readJson,
  resetRunRoot,
  resourcePlan,
  runCommand,
  setupWithRollback,
  sha256,
  validateManifest
} from './core.mjs';
import { spawn } from 'node:child_process';

class Api {
  constructor(baseUrl, runId, authToken = null) {
    this.baseUrl = baseUrl;
    this.headers = {
      'Content-Type': 'application/json',
      'X-Actor-Id': `remote-test-suite:${runId}`,
      'X-Client-Id': `remote-test-suite:${runId}`,
      'X-Task-Protocol-Version': '2',
      'X-Task-Client-Version': 'remote-test-suite/1'
    };
    if (authToken) this.headers.Authorization = `Bearer ${authToken}`;
  }
  async get(route) { return await this.request('GET', route); }
  async post(route, body) { return await this.request('POST', route, body); }
  async put(route, body) { return await this.request('PUT', route, body); }
  async request(method, route, body) {
    const response = await fetch(`${this.baseUrl}${route}`, {
      method,
      headers: this.headers,
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    if (!response.ok) throw new Error(`${method} ${route} failed (${response.status}): ${text}`);
    return text ? JSON.parse(text) : null;
  }
}

const suiteRoot = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(suiteRoot, '..', '..');
const args = parseArgs(process.argv.slice(2));
const authToken = await readAuthToken(args.authTokenFile);
const manifestPath = path.join(suiteRoot, 'scenarios', `${args.scenario}.json`);
const manifest = validateManifest(await readJson(manifestPath));
const baseRoot = path.resolve(args.root ?? path.join(repoRoot, '.tmp', 'remote-test-suite'));
const port = args.port ?? await availablePort();
const serverUrl = args.serverUrl ?? `http://127.0.0.1:${port}`;
const plan = resourcePlan({
  baseRoot,
  scenario: manifest.name,
  runId: args.runId,
  serverUrl,
  ownsServer: !args.serverUrl
});

if (args.dryRun) {
  console.log(JSON.stringify({
    dryRun: true,
    scenario: manifest.name,
    seed: args.seed,
    runId: args.runId,
    phases: expectedPhases,
    ...plan
  }, null, 2));
  process.exit(0);
}

let taskServer;
let completed = false;
try {
  await setupWithRollback([
    async () => await resetRunRoot(plan.root, baseRoot),
    async () => await seedFixture(plan.root, manifest, args.seed),
    async () => {
      if (!args.serverUrl) taskServer = await startTaskServer(plan.root, serverUrl);
    }
  ], async () => {
    taskServer?.kill('SIGTERM');
    await cleanupRunRoot(plan.root, baseRoot);
  });
  const result = await executeScenario({
    manifest,
    root: plan.root,
    serverUrl,
    seed: args.seed,
    runId: args.runId,
    authToken
  });
  completed = true;
  console.log(JSON.stringify(result, null, 2));
} finally {
  taskServer?.kill('SIGTERM');
  if (args.cleanup || !completed) await cleanupRunRoot(plan.root, baseRoot);
}

function parseArgs(values) {
  const result = { scenario: '', seed: '', runId: '', dryRun: false, cleanup: false };
  for (let index = 0; index < values.length; index++) {
    const key = values[index];
    if (key === '--dry-run') result.dryRun = true;
    else if (key === '--cleanup') result.cleanup = true;
    else if (key.startsWith('--')) {
      const name = key.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
      result[name] = values[++index];
    }
  }
  if (!result.scenario || !result.seed || !result.runId) {
    throw new Error('Usage: node tools/remote-test-suite/index.mjs --scenario NAME --seed SEED --run-id UNIQUE_ID [--auth-token-file PATH] [--dry-run] [--cleanup]');
  }
  if (!/^[A-Za-z0-9._-]{1,80}$/.test(result.seed) || !/^[A-Za-z0-9._-]{1,80}$/.test(result.runId)) {
    throw new Error('Seed and run id must use only letters, digits, dot, underscore, or hyphen.');
  }
  if (result.port) result.port = Number(result.port);
  return result;
}

async function availablePort() {
  return await new Promise((resolve, reject) => {
    const server = createServer();
    server.unref();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      server.close(() => resolve(address.port));
    });
  });
}

async function readAuthToken(file) {
  if (!file) return null;
  const resolved = path.resolve(file);
  const token = (await readFile(resolved, 'utf8')).trim();
  if (token.length < 32) throw new Error('The authentication token file must contain at least 32 characters.');
  return token;
}

async function seedFixture(root, manifest, seed) {
  const seedRepo = path.join(root, 'fixture-seed');
  const origin = path.join(root, 'fixture-origin.git');
  await mkdir(seedRepo, { recursive: true });
  await runCommand(['git', 'init', '-b', manifest.fixture.defaultBranch], { cwd: seedRepo });
  await runCommand(['git', 'config', 'user.name', 'Remote Test Suite'], { cwd: seedRepo });
  await runCommand(['git', 'config', 'user.email', 'remote-test-suite@invalid.local'], { cwd: seedRepo });
  await writeFile(path.join(seedRepo, 'package.json'), '{"name":"shipping-reference-fixture","private":true,"type":"module"}\n');
  await mkdir(path.join(seedRepo, 'src'), { recursive: true });
  await mkdir(path.join(seedRepo, 'test'), { recursive: true });
  await writeFile(path.join(seedRepo, 'src', 'index.mjs'), "export const shippingServices = ['standard'];\n");
  await writeFile(path.join(seedRepo, 'test', 'baseline.test.mjs'), "import test from 'node:test';\nimport assert from 'node:assert/strict';\ntest('baseline',()=>assert.ok(true));\n");
  await writeFile(path.join(seedRepo, 'README.md'), '# Shipping fixture\n\nStandard shipping only.\n');
  await runCommand(['git', 'add', '.'], { cwd: seedRepo });
  const date = deterministicDate(seed);
  await runCommand(['git', 'commit', '-m', 'fixture: deterministic baseline'], {
    cwd: seedRepo,
    env: { GIT_AUTHOR_DATE: date, GIT_COMMITTER_DATE: date }
  });
  await runCommand(['git', 'init', '--bare', origin], { cwd: root });
  await runCommand(['git', 'remote', 'add', 'origin', origin], { cwd: seedRepo });
  await runCommand(['git', 'push', '-u', 'origin', manifest.fixture.defaultBranch], { cwd: seedRepo });
  await runCommand(['git', '--git-dir', origin, 'symbolic-ref', 'HEAD', `refs/heads/${manifest.fixture.defaultBranch}`], { cwd: root });
}

function deterministicDate(seed) {
  const seconds = Number.parseInt(sha256(seed).slice(0, 8), 16) % (20 * 365 * 24 * 60 * 60);
  return new Date(Date.UTC(2020, 0, 1) + seconds * 1000).toISOString();
}

async function startTaskServer(root, serverUrl) {
  const child = spawn('dotnet', ['run', '--project', path.join(repoRoot, 'task-server'), '--no-launch-profile'], {
    cwd: repoRoot,
    env: {
      ...process.env,
      LISTEN_URL: serverUrl,
      TaskServer__DataDirectory: path.join(root, 'task-server-data'),
      TaskServer__BackupDirectory: path.join(root, 'task-server-backups')
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  let output = '';
  child.stdout.on('data', chunk => { output += chunk; });
  child.stderr.on('data', chunk => { output += chunk; });
  for (let attempt = 0; attempt < 240; attempt++) {
    if (child.exitCode !== null) throw new Error(`Task Server exited during startup: ${output}`);
    try {
      const response = await fetch(`${serverUrl}/readyz`);
      if (response.ok) return child;
    } catch {
      // Startup polling is bounded below.
    }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  child.kill('SIGTERM');
  throw new Error(`Task Server did not become ready: ${output}`);
}

async function executeScenario(context) {
  const { manifest, root, serverUrl, seed, runId, authToken } = context;
  const variables = { suiteRoot, repoRoot, seed, runId };
  const phaseFile = path.join(root, 'phases.jsonl');
  const outboxFile = path.join(root, 'outbox.jsonl');
  const origin = path.join(root, 'fixture-origin.git');
  const ids = Object.fromEntries(Object.entries(manifest.resources)
    .map(([key, value]) => [key, interpolate(value, variables)]));
  const api = new Api(serverUrl, runId, authToken);

  const hook = async (phase, point, detail = {}) => {
    await appendJsonl(phaseFile, { phase, point, ...detail });
    const command = manifest.hooks?.[phase];
    if (command) {
      await runCommand(command.map(value => interpolate(value, { ...variables, phase, point })), {
        cwd: root,
        env: {
          REMOTE_TEST_PHASE: phase,
          REMOTE_TEST_HOOK_POINT: point,
          REMOTE_TEST_RUN_ID: runId,
          REMOTE_TEST_SEED: seed
        }
      });
    }
  };

  await api.post('/api/v1/workspaces', { name: `Remote Test ${runId}`, workspaceId: ids.workspaceId });
  await api.post('/api/v1/projects', {
    workspaceId: ids.workspaceId,
    name: `Remote Test ${runId}`,
    taskKeyPrefix: 'RTS',
    projectId: ids.projectId
  });
  const task = await api.post(`/api/v1/projects/${ids.projectId}/tasks`, {
    title: manifest.task.title,
    body: manifest.task.body,
    state: '2-ready',
    taskId: ids.taskId,
    taskKey: manifest.task.key
  });
  const codingRunner = `coding-${runId}`;
  const codingInstance = `coding-instance-${runId}`;
  await register(api, codingRunner, codingInstance, 'coding-host', ['coding-executor']);

  await hook('claim', 'before');
  const claim = await api.post(`/api/v1/runners/${codingRunner}/claims`, {
    runnerId: codingRunner,
    instanceId: codingInstance,
    requestedTtlSeconds: 120,
    availableSlots: 1
  });
  if (claim.status !== 'claimed') throw new Error(`Reference task was not claimed: ${JSON.stringify(claim)}`);
  await hook('claim', 'after', { taskKey: claim.task.taskKey, runAttemptId: claim.run.runId, fence: claim.lease.fence });

  await hook('run', 'before');
  const codingRepo = path.join(root, 'coding', 'repo');
  const worktree = path.join(root, 'coding', 'worktree');
  await mkdir(path.dirname(codingRepo), { recursive: true });
  await runCommand(['git', 'clone', origin, codingRepo], { cwd: root });
  const branch = `runner/${codingRunner}/${manifest.task.key.toLowerCase()}`;
  await runCommand(['git', 'worktree', 'add', '-b', branch, worktree, `origin/${manifest.fixture.defaultBranch}`], { cwd: codingRepo });
  await runCommand(['git', 'config', 'user.name', 'Remote Coding Executor'], { cwd: worktree });
  await runCommand(['git', 'config', 'user.email', 'remote-coding@invalid.local'], { cwd: worktree });
  const baseSha = (await runCommand(['git', 'rev-parse', 'HEAD'], { cwd: worktree })).stdout.trim();
  await runCommand(manifest.fixture.changeCommand.map(value => interpolate(value, variables)), { cwd: worktree });
  const changed = (await runCommand(['git', 'status', '--short'], { cwd: worktree })).stdout
    .trimEnd().split(/\r?\n/).filter(Boolean).map(line => line.slice(3)).sort();
  const expected = [...manifest.fixture.expectedChangedFiles].sort();
  if (JSON.stringify(changed) !== JSON.stringify(expected)) {
    throw new Error(`Reference change touched unexpected files. Expected ${expected}; got ${changed}`);
  }
  await runCommand(['git', 'add', '.'], { cwd: worktree });
  const date = deterministicDate(seed);
  await runCommand(['git', 'commit', '-m', `feat(${manifest.task.key}): add priority shipping quotes`], {
    cwd: worktree,
    env: { GIT_AUTHOR_DATE: date, GIT_COMMITTER_DATE: date }
  });
  const resultSha = (await runCommand(['git', 'rev-parse', 'HEAD'], { cwd: worktree })).stdout.trim();
  const resultRef = `refs/heads/agent-studio/results/${claim.run.runId}/${resultSha}`;
  await runCommand(['git', 'push', 'origin', `${branch}:${branch}`, `HEAD:${resultRef}`], { cwd: worktree });
  await hook('run', 'after', { resultSha, resultRef });

  await hook('gate', 'before');
  const gate = await runCommand(manifest.fixture.acceptanceCommand, { cwd: worktree });
  await hook('gate', 'after', { status: 'pass', outputSha256: sha256(gate.stdout + gate.stderr) });

  const repositoryUrl = pathToFileURL(origin).href;
  const repositoryId = `repo_${sha256(repositoryUrl.trim().replace(/\/$/, '').toLowerCase())}`;
  const manifestDigest = sha256('[]');
  const envelope = {
    repositoryId,
    sourceRunAttemptId: claim.run.runId,
    baseSha,
    resultSha,
    immutableRemoteRef: resultRef,
    sourceBundleDigest: null,
    artifactManifestDigest: manifestDigest,
    submodules: [],
    lfsObjects: [],
    repositoryUrl
  };
  const digestEnvelope = { ...envelope, repositoryUrl: null };
  const envelopeDigest = sha256(JSON.stringify(digestEnvelope));
  const handoff = {
    runnerId: codingRunner,
    instanceId: codingInstance,
    leaseId: claim.lease.leaseId,
    fence: claim.lease.fence,
    sequence: 1,
    idempotencyKey: `${runId}:handoff`,
    envelopeDigest,
    envelope
  };
  await appendJsonl(outboxFile, { sequence: 1, kind: 'result-handoff', acknowledged: false, payload: handoff });
  await api.put(`/api/v1/runs/${claim.run.runId}/result-handoff`, handoff);
  await appendJsonl(outboxFile, { sequence: 1, kind: 'result-handoff', acknowledged: true });
  const completion = {
    runnerId: codingRunner,
    instanceId: codingInstance,
    leaseId: claim.lease.leaseId,
    fence: claim.lease.fence,
    outcome: 'Done',
    summary: 'Deterministic reference change completed and gated.',
    resultEnvelopeDigest: envelopeDigest,
    idempotencyKey: `${runId}:completion`,
    sequence: 2
  };
  await appendJsonl(outboxFile, { sequence: 2, kind: 'completion', acknowledged: false, payload: completion });
  await api.post(`/api/v1/runs/${claim.run.runId}/completion`, completion);
  await appendJsonl(outboxFile, { sequence: 2, kind: 'completion', acknowledged: true });

  await hook('review', 'before');
  const subject = await api.post('/api/v1/reviews/subjects', {
    taskId: task.taskId,
    sourceRunId: claim.run.runId,
    repositoryId,
    repositoryUrl,
    expectedResultSha: resultSha,
    resultRef,
    sourceBundleArtifactId: null,
    sourceBundleSha256: null,
    codingHostId: 'coding-host',
    reviewPolicyHash: sha256('reference-review-policy-v1'),
    plan: {
      commands: [{
        stepId: 'semantic-acceptance',
        aspect: 'semantic',
        fileName: manifest.fixture.acceptanceCommand[0],
        arguments: manifest.fixture.acceptanceCommand.slice(1),
        required: true,
        timeoutSeconds: 120,
        compareToBaseline: false
      }],
      requiredAspects: ['semantic'],
      requiresVisualReview: false,
      requireDifferentHostFailureDomain: false,
      integrationRef: manifest.fixture.defaultBranch
    },
    idempotencyKey: `${runId}:review-subject`
  });
  const reviewRunner = `review-${runId}`;
  const reviewInstance = `review-instance-${runId}`;
  await register(api, reviewRunner, reviewInstance, 'review-host', ['review-executor', 'review:git', 'review:semantic']);
  const reviewClaim = await api.post(`/api/v1/runners/${reviewRunner}/review-claims`, {
    executorId: reviewRunner,
    instanceId: reviewInstance,
    requestedTtlSeconds: 120,
    availableSlots: 1
  });
  if (reviewClaim.status !== 'claimed') throw new Error(`Review was not claimed: ${JSON.stringify(reviewClaim)}`);
  const reviewRepo = path.join(root, 'review', 'repo');
  await mkdir(path.dirname(reviewRepo), { recursive: true });
  await runCommand(['git', 'clone', origin, reviewRepo], { cwd: root });
  await runCommand(['git', 'fetch', 'origin', `${resultRef}:${resultRef}`], { cwd: reviewRepo });
  await runCommand(['git', 'checkout', '--detach', resultSha], { cwd: reviewRepo });
  const reviewHead = (await runCommand(['git', 'rev-parse', 'HEAD'], { cwd: reviewRepo })).stdout.trim();
  if (reviewHead !== resultSha) throw new Error(`Exact-SHA review mismatch: ${reviewHead} != ${resultSha}`);
  const treeSha = (await runCommand(['git', 'rev-parse', 'HEAD^{tree}'], { cwd: reviewRepo })).stdout.trim();
  const reviewStarted = new Date().toISOString();
  const reviewTest = await runCommand(manifest.fixture.acceptanceCommand, { cwd: reviewRepo });
  const reviewFinished = new Date().toISOString();
  await api.post(`/api/v1/reviews/attempts/${reviewClaim.attempt.attemptId}/report`, {
    executorId: reviewRunner,
    instanceId: reviewInstance,
    leaseId: reviewClaim.lease.leaseId,
    fence: reviewClaim.lease.fence,
    authorityEpoch: reviewClaim.lease.authorityEpoch,
    idempotencyKey: `${runId}:review-report`,
    outcome: 'Pass',
    failureClassification: null,
    summary: 'Exact Result-SHA semantic acceptance passed.',
    workspace: {
      repositoryId,
      expectedResultSha: resultSha,
      actualHead: reviewHead,
      treeHash: treeSha,
      dirtyBefore: false,
      dirtyAfter: false,
      workspaceIdentity: `review-${runId}`,
      resourceNamespace: reviewClaim.lease.resourceNamespace
    },
    environment: {
      hostId: 'review-host',
      executorId: reviewRunner,
      instanceId: reviewInstance,
      osDescription: process.platform,
      architecture: process.arch,
      runtimeVersion: process.version,
      toolchain: { node: process.version },
      isolation: { root: reviewRepo }
    },
    commands: [{
      stepId: 'semantic-acceptance',
      aspect: 'semantic',
      fileName: manifest.fixture.acceptanceCommand[0],
      arguments: manifest.fixture.acceptanceCommand.slice(1),
      expectedResultSha: resultSha,
      headBefore: reviewHead,
      treeBefore: treeSha,
      startedAt: reviewStarted,
      finishedAt: reviewFinished,
      exitCode: 0,
      signal: null,
      stdoutSha256: sha256(reviewTest.stdout),
      stderrSha256: sha256(reviewTest.stderr)
    }],
    artifacts: [],
    verdicts: [{ aspect: 'semantic', status: 'pass', classification: 'Accepted', summary: 'All deterministic acceptance tests passed.' }]
  });
  await api.post(`/api/v1/reviews/attempts/${reviewClaim.attempt.attemptId}/cleanup`, {
    executorId: reviewRunner,
    instanceId: reviewInstance,
    leaseId: reviewClaim.lease.leaseId,
    fence: reviewClaim.lease.fence,
    authorityEpoch: reviewClaim.lease.authorityEpoch,
    idempotencyKey: `${runId}:review-cleanup`,
    workspaceRemoved: true
  });
  await hook('review', 'after', { subjectId: subject.subjectId, reviewAttemptId: reviewClaim.attempt.attemptId, resultSha });

  await hook('integration', 'before');
  const integrationRepo = path.join(root, 'integration', 'repo');
  await mkdir(path.dirname(integrationRepo), { recursive: true });
  await runCommand(['git', 'clone', origin, integrationRepo], { cwd: root });
  await runCommand(['git', 'config', 'user.name', 'Remote Test Integrator'], { cwd: integrationRepo });
  await runCommand(['git', 'config', 'user.email', 'remote-integration@invalid.local'], { cwd: integrationRepo });
  await runCommand(['git', 'fetch', 'origin', `${resultRef}:refs/remotes/origin/reference-result`], { cwd: integrationRepo });
  const fetched = (await runCommand(['git', 'rev-parse', 'refs/remotes/origin/reference-result'], { cwd: integrationRepo })).stdout.trim();
  if (fetched !== resultSha) throw new Error(`Integration refused mutable result: ${fetched} != ${resultSha}`);
  await runCommand(['git', 'checkout', manifest.fixture.defaultBranch], { cwd: integrationRepo });
  await runCommand(['git', 'merge', '--no-ff', '--no-edit', resultSha], {
    cwd: integrationRepo,
    env: { GIT_AUTHOR_DATE: date, GIT_COMMITTER_DATE: date }
  });
  await runCommand(manifest.fixture.acceptanceCommand, { cwd: integrationRepo });
  await runCommand(['git', 'push', 'origin', manifest.fixture.defaultBranch], { cwd: integrationRepo });
  const integratedTree = (await runCommand(['git', 'rev-parse', 'HEAD^{tree}'], { cwd: integrationRepo })).stdout.trim();
  const currentTask = await api.get(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`);
  await api.put(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`, {
    title: null,
    body: null,
    state: '6-completed',
    expectedVersion: currentTask.version
  });
  await hook('integration', 'after', { status: 'integrated', resultSha, semanticTree: integratedTree });

  const phaseEvents = (await readFile(phaseFile, 'utf8')).trim().split(/\r?\n/).map(JSON.parse);
  return {
    scenario: manifest.name,
    seed,
    runId,
    accepted: true,
    resultSha,
    semanticTree: integratedTree,
    phaseSequence: phaseEvents.filter(event => event.point === 'after').map(event => event.phase),
    resourcesRoot: root
  };
}

async function register(api, runnerId, instanceId, hostId, capabilities) {
  await api.put(`/api/v1/runners/${runnerId}`, {
    name: runnerId,
    hostId,
    instanceId,
    runnerVersion: 'remote-test-suite/1',
    protocolVersion: 2,
    capabilities
  });
}

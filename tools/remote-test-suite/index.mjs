#!/usr/bin/env node
import { createServer } from 'node:net';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import {
  appendJsonl,
  CommandTimeoutError,
  cleanupRunRoot,
  exists,
  expectedPhases,
  interpolate,
  readJson,
  resetRunRoot,
  resourcePlan,
  runCommand,
  scenarioAssertions,
  setupWithRollback,
  sha256,
  validateManifest
} from './core.mjs';
import {
  assertFaultActivationRequest,
  FaultController,
  faultActivationToken,
  InjectedNetworkFault,
  initializeFaultSafetyMarker,
  resolveFaultSelection,
  validateFaultCatalog
} from './faults.mjs';
import { spawn } from 'node:child_process';
import { executeHistoricalReplay } from './replay-scenarios.mjs';

class Api {
  constructor(baseUrl, runId, faults, authToken = null) {
    this.baseUrl = baseUrl;
    this.faults = faults;
    this.attempts = new Map();
    this.headers = {
      'Content-Type': 'application/json',
      'X-Actor-Id': `remote-test-suite:${runId}`,
      'X-Client-Id': `remote-test-suite:${runId}`,
      'X-Task-Protocol-Version': '2',
      'X-Task-Client-Version': 'remote-test-suite/1'
    };
    if (authToken) this.headers.Authorization = `Bearer ${authToken}`;
  }
  async get(route, options) { return await this.request('GET', route, undefined, options); }
  async post(route, body, options) { return await this.request('POST', route, body, options); }
  async put(route, body, options) { return await this.request('PUT', route, body, options); }
  async request(method, route, body, { operation, maxAttempts = 3 } = {}) {
    let lastError;
    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      if (operation) this.attempts.set(operation, (this.attempts.get(operation) ?? 0) + 1);
      try {
        const injection = operation ? await this.faults.next(operation) : null;
        if (injection?.action === 'disconnect-before-send') {
          throw new InjectedNetworkFault(operation, injection.action, injection.occurrence);
        }
        const result = await this.send(method, route, body);
        if (injection?.action === 'disconnect-after-commit') {
          throw new InjectedNetworkFault(operation, injection.action, injection.occurrence);
        }
        return result;
      } catch (error) {
        if (!(error instanceof InjectedNetworkFault) || attempt === maxAttempts) throw error;
        lastError = error;
      }
    }
    throw lastError;
  }
  async send(method, route, body) {
    const result = await this.attempt(method, route, body);
    if (!result.ok) {
      const error = new Error(`${method} ${route} failed (${result.status}): ${result.text}`);
      error.status = result.status;
      error.body = result.text;
      throw error;
    }
    return result.value;
  }
  // Historical replay scenarios use one non-throwing attempt to inspect
  // expected stale-writer and fencing rejections.
  async attempt(method, route, body) {
    const response = await fetch(`${this.baseUrl}${route}`, {
      method,
      headers: this.headers,
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    let value = null;
    if (text) {
      try { value = JSON.parse(text); }
      catch { value = text; }
    }
    return { ok: response.ok, status: response.status, text, value };
  }
  snapshot() {
    return Object.fromEntries(this.attempts);
  }
}

const suiteRoot = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(suiteRoot, '..', '..');
const args = parseArgs(process.argv.slice(2));
const authToken = await readAuthToken(args.authTokenFile);
const manifestPath = path.join(suiteRoot, 'scenarios', `${args.scenario}.json`);
const manifest = validateManifest(await readJson(manifestPath));
if (manifest.name !== args.scenario) {
  throw new Error(
    `Scenario file '${args.scenario}.json' declares mismatched name '${manifest.name}'.`);
}
const catalog = validateFaultCatalog(
  await readJson(path.join(suiteRoot, 'fault-catalog.json')));
const selectedFaults = resolveFaultSelection(catalog, manifest.faults ?? []);
const baseRoot = path.resolve(args.root ?? path.join(repoRoot, '.tmp', 'remote-test-suite'));
const port = args.port ?? await availablePort();
const serverUrl = args.serverUrl ?? `http://127.0.0.1:${port}`;
const plan = resourcePlan({
  baseRoot,
  scenario: manifest.name,
  runId: args.runId,
  serverUrl,
  ownsServer: !args.serverUrl,
  faults: selectedFaults.map(fault => fault.id)
});
const activation = selectedFaults.length === 0
  ? null
  : faultActivationToken({
      scenario: manifest.name,
      runId: args.runId,
      root: plan.root
    });

if (args.dryRun) {
  console.log(JSON.stringify({
    dryRun: true,
    scenario: manifest.name,
    seed: args.seed,
    runId: args.runId,
    phases: expectedPhases,
    faults: selectedFaults.map(fault => ({
      id: fault.id,
      incidentClass: fault.incidentClass,
      anchors: fault.anchors,
      schedule: fault.schedule
    })),
    faultActivation: activation === null
      ? { required: false }
      : {
          required: true,
          enableFlag: '--enable-faults',
          acknowledgementFlag: '--fault-ack',
          acknowledgement: activation,
          isolatedTaskServerRequired: true
        },
    ...plan
  }, null, 2));
  process.exit(0);
}

let taskServer;
let completed = false;
let faults;
try {
  assertFaultActivationRequest({
    root: plan.root,
    scenario: manifest.name,
    runId: args.runId,
    selectedFaults,
    enabled: args.enableFaults,
    acknowledgement: args.faultAck,
    ownsServer: !args.serverUrl
  });
  await setupWithRollback([
    async () => await resetRunRoot(plan.root, baseRoot),
    async () => {
      const marker = await initializeFaultSafetyMarker({
        root: plan.root,
        scenario: manifest.name,
        runId: args.runId,
        selectedFaults,
        enabled: args.enableFaults,
        acknowledgement: args.faultAck,
        ownsServer: !args.serverUrl
      });
      faults = new FaultController({
        root: plan.root,
        selectedFaults,
        marker
      });
    },
    async () => await seedFixture(plan.root, manifest, args.seed),
    async () => {
      if (!args.serverUrl) taskServer = await startTaskServer(plan.root, serverUrl);
    }
  ], async () => {
    await stopChild(taskServer);
    await cleanupRunRoot(plan.root, baseRoot);
  });
  const result = await executeScenario({
    manifest,
    root: plan.root,
    serverUrl,
    seed: args.seed,
    runId: args.runId,
    faults,
    authToken
  });
  await writeFile(path.join(plan.root, 'result.json'), `${JSON.stringify(result, null, 2)}\n`);
  completed = true;
  console.log(JSON.stringify(result, null, 2));
} finally {
  await stopChild(taskServer);
  if (args.cleanup || (selectedFaults.length > 0 && !args.keep) || !completed) {
    await cleanupRunRoot(plan.root, baseRoot);
  }
}

function parseArgs(values) {
  const result = {
    scenario: '',
    seed: '',
    runId: '',
    dryRun: false,
    cleanup: false,
    keep: false,
    enableFaults: false,
    faultAck: ''
  };
  for (let index = 0; index < values.length; index++) {
    const key = values[index];
    if (key === '--dry-run') result.dryRun = true;
    else if (key === '--cleanup') result.cleanup = true;
    else if (key === '--keep') result.keep = true;
    else if (key === '--enable-faults') result.enableFaults = true;
    else if (key.startsWith('--')) {
      if (index + 1 >= values.length || values[index + 1].startsWith('--')) {
        throw new Error(`Missing value for ${key}.`);
      }
      const name = key.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
      result[name] = values[++index];
    }
  }
  if (!result.scenario || !result.seed || !result.runId) {
    throw new Error('Usage: node tools/remote-test-suite/index.mjs --scenario NAME --seed SEED --run-id UNIQUE_ID [--auth-token-file PATH] [--dry-run] [--cleanup|--keep] [--enable-faults --fault-ack TOKEN]');
  }
  if (!/^[A-Za-z0-9._-]{1,80}$/.test(result.seed) || !/^[A-Za-z0-9._-]{1,80}$/.test(result.runId)) {
    throw new Error('Seed and run id must use only letters, digits, dot, underscore, or hyphen.');
  }
  if (result.port) result.port = Number(result.port);
  if (result.cleanup && result.keep) throw new Error('--cleanup and --keep are mutually exclusive.');
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

async function stopChild(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  const closed = new Promise(resolve => child.once('close', resolve));
  child.kill('SIGTERM');
  let timeout;
  const stopped = await Promise.race([
    closed.then(() => true),
    new Promise(resolve => {
      timeout = setTimeout(() => resolve(false), 5000);
    })
  ]);
  clearTimeout(timeout);
  if (!stopped && child.exitCode === null && child.signalCode === null) {
    child.kill('SIGKILL');
    await closed;
  }
}

async function executeScenario(context) {
  const { manifest, root, serverUrl, seed, runId, faults, authToken } = context;
  const variables = { suiteRoot, repoRoot, seed, runId };
  const phaseFile = path.join(root, 'phases.jsonl');
  const outboxFile = path.join(root, 'outbox.jsonl');
  const origin = path.join(root, 'fixture-origin.git');
  const incidentEvidence = [];
  const processEvidence = [];
  const scenarioStartedAt = new Date().toISOString();
  const scenarioStartedMonotonic = process.hrtime.bigint();
  let phaseEventSequence = 0;
  const ids = Object.fromEntries(Object.entries(manifest.resources)
    .map(([key, value]) => [key, interpolate(value, variables)]));
  const api = new Api(serverUrl, runId, faults, authToken);

  const hook = async (phase, point, detail = {}) => {
    const monotonicMs = Number(
      (process.hrtime.bigint() - scenarioStartedMonotonic) / 1_000_000n);
    await appendJsonl(phaseFile, {
      sequence: ++phaseEventSequence,
      phase,
      point,
      recordedAt: new Date().toISOString(),
      monotonicMs,
      scenarioStartedAt,
      ...detail
    });
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

  if (manifest.contract) {
    return await executeHistoricalReplay({
      ...context,
      api,
      hook,
      suiteRoot,
      repoRoot,
      register
    });
  }
  const checks = scenarioAssertions(manifest);

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
  }, { operation: 'claim' });
  if (claim.status !== 'claimed') throw new Error(`Reference task was not claimed: ${JSON.stringify(claim)}`);
  await hook('claim', 'after', { taskKey: claim.task.taskKey, runAttemptId: claim.run.runId, fence: claim.lease.fence });

  await api.post(`/api/v1/runs/${claim.run.runId}/lease/renew`, {
    runnerId: codingRunner,
    instanceId: codingInstance,
    leaseId: claim.lease.leaseId,
    fence: claim.lease.fence,
    requestedTtlSeconds: 120,
    inventory: {
      observedAt: claim.lease.acquiredAt,
      processes: []
    }
  }, { operation: 'heartbeat' });

  await hook('run', 'before');
  const codingRepo = path.join(root, 'coding', 'repo');
  const worktree = path.join(root, 'coding', 'worktree');
  await mkdir(path.dirname(codingRepo), { recursive: true });
  await runCommand(['git', 'clone', origin, codingRepo], { cwd: root });
  const branch = `runner/${codingRunner}/${manifest.task.key.toLowerCase()}`;
  const preparation = await prepareWorktree({
    faults,
    codingRepo,
    worktree,
    branch,
    defaultBranch: manifest.fixture.defaultBranch
  });
  if (!preparation.ready) {
    incidentEvidence.push({
      incidentClass: 'worktree-collision',
      outcome: 'worktree-blocked',
      busyPath: worktree,
      attempts: preparation.attempts,
      foreignMarkerSha256: preparation.foreignMarkerSha256,
      foreignPreserved: preparation.foreignPreserved,
      foreignRegistered: preparation.foreignRegistered,
      foreignHead: preparation.foreignHead
    });
    const event = {
      eventId: `evt-${runId}-worktree-blocked`,
      kind: 'runner.worktree-blocked',
      payloadJson: JSON.stringify(incidentEvidence.at(-1)),
      idempotencyKey: `${runId}:worktree-blocked`,
      fence: claim.lease.fence,
      runnerId: codingRunner,
      instanceId: codingInstance,
      leaseId: claim.lease.leaseId,
      sequence: 1
    };
    await appendJsonl(outboxFile, {
      sequence: 1,
      kind: 'event',
      acknowledged: false,
      payload: event
    });
    await api.post(`/api/v1/runs/${claim.run.runId}/events`, event);
    await appendJsonl(outboxFile, {
      sequence: 1,
      kind: 'event',
      acknowledged: true
    });
    const released = await api.post(`/api/v1/runs/${claim.run.runId}/lease/release`, {
      runnerId: codingRunner,
      instanceId: codingInstance,
      leaseId: claim.lease.leaseId,
      fence: claim.lease.fence,
      outcome: 'worktree-blocked'
    });
    const readyTask = await api.get(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`);
    const humanTask = await api.put(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`, {
      title: null,
      body: null,
      state: '5-human-review',
      expectedVersion: readyTask.version
    });
    await hook('run', 'after', {
      status: 'worktree-blocked',
      busyPath: worktree,
      attempts: preparation.attempts
    });
    checks.check(
      'worktree-collision-contained',
      humanTask.state === '5-human-review'
        && preparation.attempts === 5
        && preparation.foreignPreserved
        && preparation.foreignRegistered,
      `foreign worktree remained registered and unchanged after ${preparation.attempts} bounded attempts`);
    const report = await finishScenario({
      manifest,
      root,
      seed,
      runId,
      origin,
      ids,
      task,
      claim,
      api,
      faults,
      phaseFile,
      outboxFile,
      incidentEvidence,
      processEvidence,
      accepted: false,
      resultSha: null,
      semanticTree: null,
      incidentOutcome: 'worktree-blocked',
      leaseStatus: released.lease?.status ?? released.status,
      worktreeEvidence: {
        path: worktree,
        isolated: true,
        ready: false,
        busyPathReported: worktree,
        prepareAttempts: preparation.attempts,
        foreignPreserved: preparation.foreignPreserved,
        foreignRegistered: preparation.foreignRegistered,
        foreignHead: preparation.foreignHead
      }
    });
    return {
      ...report,
      ...checks.finish(humanTask.state, 0)
    };
  }
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
  const gateResult = await runGate({
    faults,
    root,
    worktree,
    acceptanceCommand: manifest.fixture.acceptanceCommand
  });
  incidentEvidence.push(...gateResult.incidents);
  processEvidence.push(...gateResult.processEvidence);
  await hook('gate', 'after', {
    status: 'pass',
    recovery: gateResult.recovered ? 'bounded-retry' : 'not-needed',
    classification: gateResult.recovered ? 'infrastructure-timeout' : 'product-pass',
    productTestFailure: false,
    outputSha256: sha256(gateResult.gate.stdout + gateResult.gate.stderr)
  });

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
  const terminalInjection = await faults.next('terminal-marker');
  const terminalFacts = terminalFactsFor(terminalInjection, {
    runAttemptId: claim.run.runId,
    resultSha,
    resultRef
  });
  if (terminalInjection) {
    incidentEvidence.push({
      incidentClass: 'terminal-marker-loss',
      action: terminalInjection.action,
      outcome: terminalFacts.proofComplete
        ? 'lost-terminal-marker-recovered'
        : 'protocol-inconclusive-human-terminal',
      proofComplete: terminalFacts.proofComplete
    });
  }
  const terminalEvent = {
    eventId: `evt-${runId}-terminal-fact`,
    kind: 'runner.terminal-fact',
    payloadJson: JSON.stringify(terminalFacts),
    idempotencyKey: `${runId}:terminal-fact`,
    fence: claim.lease.fence,
    runnerId: codingRunner,
    instanceId: codingInstance,
    leaseId: claim.lease.leaseId,
    sequence: 1
  };
  await appendJsonl(outboxFile, {
    sequence: 1,
    kind: 'event',
    acknowledged: false,
    payload: terminalEvent
  });
  await api.post(
    `/api/v1/runs/${claim.run.runId}/events`,
    terminalEvent,
    { operation: 'event-report' });
  await appendJsonl(outboxFile, {
    sequence: 1,
    kind: 'event',
    acknowledged: true
  });

  const evidenceContent = Buffer.from(
    `${JSON.stringify({ resultSha, resultRef, terminalFacts })}\n`,
    'utf8');
  const artifact = {
    artifactId: `art-${runId}-terminal-evidence`,
    name: 'terminal-evidence.json',
    mediaType: 'application/json',
    contentBase64: evidenceContent.toString('base64'),
    sha256: sha256(evidenceContent),
    idempotencyKey: `${runId}:terminal-evidence`,
    fence: claim.lease.fence,
    runnerId: codingRunner,
    instanceId: codingInstance,
    leaseId: claim.lease.leaseId,
    sequence: 2
  };
  await appendJsonl(outboxFile, {
    sequence: 2,
    kind: 'artifact',
    acknowledged: false,
    payload: artifact
  });
  await api.post(
    `/api/v1/runs/${claim.run.runId}/artifacts`,
    artifact,
    { operation: 'artifact-report' });
  await appendJsonl(outboxFile, {
    sequence: 2,
    kind: 'artifact',
    acknowledged: true
  });

  const handoff = {
    runnerId: codingRunner,
    instanceId: codingInstance,
    leaseId: claim.lease.leaseId,
    fence: claim.lease.fence,
    sequence: 3,
    idempotencyKey: `${runId}:handoff`,
    envelopeDigest,
    envelope
  };
  await appendJsonl(outboxFile, { sequence: 3, kind: 'result-handoff', acknowledged: false, payload: handoff });
  await api.put(`/api/v1/runs/${claim.run.runId}/result-handoff`, handoff);
  await appendJsonl(outboxFile, { sequence: 3, kind: 'result-handoff', acknowledged: true });
  const completion = {
    runnerId: codingRunner,
    instanceId: codingInstance,
    leaseId: claim.lease.leaseId,
    fence: claim.lease.fence,
    outcome: terminalFacts.proofComplete ? 'Done' : 'ProtocolInconclusive',
    summary: terminalFacts.proofComplete
      ? 'Deterministic reference change completed with durable terminal and Result-SHA proof.'
      : 'Terminal proof was interrupted. Result-SHA was retained for inspection without claiming success.',
    resultEnvelopeDigest: envelopeDigest,
    idempotencyKey: `${runId}:completion`,
    sequence: 4
  };
  await appendJsonl(outboxFile, { sequence: 4, kind: 'completion', acknowledged: false, payload: completion });
  await api.post(
    `/api/v1/runs/${claim.run.runId}/completion`,
    completion,
    { operation: 'completion-report' });
  await appendJsonl(outboxFile, { sequence: 4, kind: 'completion', acknowledged: true });

  if (!terminalFacts.proofComplete) {
    const reviewTask = await api.get(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`);
    const humanTask = await api.put(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`, {
      title: null,
      body: null,
      state: '5-human-review',
      expectedVersion: reviewTask.version
    });
    checks.check(
      'inconclusive-terminal-contained',
      humanTask.state === '5-human-review'
        && terminalFacts.proofComplete === false
        && resultSha !== null,
      `incomplete terminal proof retained Result-SHA ${resultSha} in ${humanTask.state}`);
    const report = await finishScenario({
      manifest,
      root,
      seed,
      runId,
      origin,
      ids,
      task,
      claim,
      api,
      faults,
      phaseFile,
      outboxFile,
      incidentEvidence,
      processEvidence,
      accepted: false,
      resultSha,
      semanticTree: null,
      incidentOutcome: combineIncidentOutcome(
        faults.ids,
        'protocol-inconclusive-human-terminal'),
      leaseStatus: 'completed',
      worktreeEvidence: {
        path: worktree,
        isolated: true,
        ready: true,
        busyPathReported: null,
        prepareAttempts: preparation.attempts,
        foreignPreserved: true
      }
    });
    return {
      ...report,
      ...checks.finish(humanTask.state, 0)
    };
  }

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
  const terminalTask = await api.put(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`, {
    title: null,
    body: null,
    state: '6-completed',
    expectedVersion: currentTask.version
  });
  await hook('integration', 'after', { status: 'integrated', resultSha, semanticTree: integratedTree });

  const phaseEvents = (await readFile(phaseFile, 'utf8')).trim().split(/\r?\n/).map(JSON.parse);
  checks.check(
    'reference-change-accepted',
    terminalTask.state === '6-completed'
      && resultSha === fetched
      && phaseEvents.filter(event => event.point === 'after').length === expectedPhases.length,
    `exact result ${resultSha} reached ${terminalTask.state} through all five phases`);
  const recoveryUsed = Object.values(api.snapshot())
    .reduce((sum, count) => sum + Math.max(0, count - 1), 0)
    + (gateResult.recovered ? 1 : 0);
  const contract = checks.finish(terminalTask.state, recoveryUsed);
  const report = await finishScenario({
    manifest,
    root,
    seed,
    runId,
    origin,
    ids,
    task,
    claim,
    api,
    faults,
    phaseFile,
    outboxFile,
    incidentEvidence,
    processEvidence,
    accepted: true,
    resultSha,
    semanticTree: integratedTree,
    incidentOutcome: combineIncidentOutcome(faults.ids, 'recovered'),
    leaseStatus: 'completed',
    worktreeEvidence: {
      path: worktree,
      isolated: true,
      ready: true,
      busyPathReported: null,
      prepareAttempts: preparation.attempts,
      foreignPreserved: true
    }
  });
  return {
    ...report,
    ...contract
  };
}

async function prepareWorktree({
  faults,
  codingRepo,
  worktree,
  branch,
  defaultBranch
}) {
  const maxAttempts = faults.has('worktree-target-collision') ? 5 : 1;
  const foreignMarker = path.join(worktree, 'foreign-owner.txt');
  const markerContent = 'fixture-owned foreign worktree occupant\n';
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    const injection = await faults.next('worktree-prepare');
    if (injection?.action === 'occupy-target'
        && injection.parameters.maxPrepareAttempts !== maxAttempts) {
      throw new Error('Worktree collision schedule does not match the bounded prepare loop.');
    }
    if (injection?.action === 'occupy-target' && !await exists(foreignMarker)) {
      await runCommand([
        'git',
        'worktree',
        'add',
        '--detach',
        worktree,
        `origin/${defaultBranch}`
      ], { cwd: codingRepo });
      await writeFile(foreignMarker, markerContent);
    }
    if (await exists(worktree)) {
      if (attempt < maxAttempts) continue;
      const persisted = await readFile(foreignMarker, 'utf8');
      const foreignHead = (await runCommand(
        ['git', 'rev-parse', 'HEAD'],
        { cwd: worktree })).stdout.trim();
      const registry = (await runCommand(
        ['git', 'worktree', 'list', '--porcelain'],
        { cwd: codingRepo })).stdout;
      return {
        ready: false,
        attempts: attempt,
        foreignMarkerSha256: sha256(persisted),
        foreignPreserved: persisted === markerContent,
        foreignRegistered: registry.includes(`worktree ${worktree}\n`),
        foreignHead
      };
    }
    await runCommand([
      'git',
      'worktree',
      'add',
      '-b',
      branch,
      worktree,
      `origin/${defaultBranch}`
    ], { cwd: codingRepo });
    return {
      ready: true,
      attempts: attempt,
      foreignMarkerSha256: null,
      foreignPreserved: true,
      foreignRegistered: false,
      foreignHead: null
    };
  }
  throw new Error('Worktree preparation exhausted without a terminal result.');
}

async function runGate({ faults, root, worktree, acceptanceCommand }) {
  const injection = await faults.next('gate-command');
  const incidents = [];
  const processEvidence = [];
  if (injection?.action === 'watchdog-timeout') {
    if (injection.parameters.maxRecoveryAttempts !== 1) {
      throw new Error('Gate timeout fixture supports exactly one bounded recovery attempt.');
    }
    const marker = path.join(root, 'gate-timeout-processes.json');
    try {
      await runCommand([
        process.execPath,
        path.join(suiteRoot, 'fixtures', 'gate-timeout.mjs'),
        '--marker',
        marker,
        '--delay-ms',
        String(injection.parameters.fixtureDelayMs)
      ], {
        cwd: worktree,
        timeoutMs: injection.parameters.timeoutMs
      });
      throw new Error('Gate timeout fixture unexpectedly completed inside the watchdog.');
    } catch (error) {
      if (!(error instanceof CommandTimeoutError)) throw error;
      const pids = JSON.parse(await readFile(marker, 'utf8'));
      const reaped = await waitForPidsGone([pids.parentPid, pids.childPid]);
      if (!reaped) {
        throw new Error(`Gate timeout process tree survived watchdog: ${JSON.stringify(pids)}`);
      }
      processEvidence.push({
        operation: 'gate-command',
        parentPid: pids.parentPid,
        childPid: pids.childPid,
        reaped: true
      });
      incidents.push({
        incidentClass: 'gate-timeout',
        outcome: 'gate-timeout-recovered',
        classification: error.classification,
        productTestFailure: error.productTestFailure,
        watchdogMs: error.timeoutMs,
        recoveryAttempts: 1,
        processTreeReaped: true
      });
    }
  }
  const gate = await runCommand(acceptanceCommand, { cwd: worktree });
  return {
    gate,
    recovered: incidents.length > 0,
    incidents,
    processEvidence
  };
}

async function waitForPidsGone(pids) {
  for (let attempt = 0; attempt < 40; attempt++) {
    if (pids.every(pid => !processExists(pid))) return true;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  return pids.every(pid => !processExists(pid));
}

function processExists(pid) {
  if (!Number.isInteger(pid) || pid < 1) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    if (error?.code === 'ESRCH') return false;
    throw error;
  }
}

function terminalFactsFor(injection, { runAttemptId, resultSha, resultRef }) {
  const interrupted = injection?.action === 'interrupt-marker';
  const dropped = injection?.action === 'drop-sentinel';
  return {
    classifierVersion: 'remote-test-suite-terminal-facts/v1',
    runAttemptId,
    sentinel: dropped || interrupted ? null : 'TASK_DONE',
    providerTerminalEvent: interrupted ? null : 'response.completed',
    exitCode: interrupted ? null : 0,
    durableOutputState: 'acknowledged',
    resultSha,
    resultRef,
    proofComplete: !interrupted,
    recoveryAction: interrupted ? 'ask-for-human-input' : 'retry-handoff'
  };
}

function combineIncidentOutcome(faultIds, fallback) {
  if (faultIds.length === 0) return 'none';
  const outcomes = [];
  if (faultIds.includes('task-server-network-blips')) outcomes.push('network-blips-replayed');
  if (faultIds.includes('gate-watchdog-timeout')) outcomes.push('gate-timeout-recovered');
  if (faultIds.includes('lost-completion-sentinel')) outcomes.push('lost-terminal-marker-recovered');
  if (faultIds.includes('interrupted-terminal-marker')) outcomes.push(fallback);
  if (faultIds.includes('worktree-target-collision')) outcomes.push('worktree-blocked');
  return outcomes.join('+') || fallback;
}

async function finishScenario({
  manifest,
  root,
  seed,
  runId,
  origin,
  ids,
  task,
  claim,
  api,
  faults,
  phaseFile,
  outboxFile,
  incidentEvidence,
  processEvidence,
  accepted,
  resultSha,
  semanticTree,
  incidentOutcome,
  leaseStatus,
  worktreeEvidence
}) {
  faults.assertConsumed();
  const currentTask = await api.get(
    `/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`);
  const attempts = await api.get(
    `/api/v1/projects/${ids.projectId}/tasks/${task.taskId}/attempts`);
  const history = await api.get(
    `/api/v1/projects/${ids.projectId}/tasks/${task.taskId}/history`);
  const handoff = resultSha
    ? await api.get(`/api/v1/runs/${claim.run.runId}/result-handoff`)
    : null;
  const phaseEvents = await readJsonl(phaseFile);
  const outboxRows = await readJsonl(outboxFile);
  const logicalOutbox = outboxRows.filter(row => row.acknowledged === false);
  const acknowledged = new Set(
    outboxRows.filter(row => row.acknowledged === true).map(row => row.sequence));
  const originHead = (await runCommand([
    'git',
    '--git-dir',
    origin,
    'rev-parse',
    manifest.fixture.defaultBranch
  ], { cwd: root })).stdout.trim();
  const resultIntegrated = resultSha
    ? (await runCommand([
        'git',
        '--git-dir',
        origin,
        'merge-base',
        '--is-ancestor',
        resultSha,
        manifest.fixture.defaultBranch
      ], { cwd: root }).then(() => true, () => false))
    : false;
  const assertions = {
    lane: {
      actual: currentTask.state,
      expected: manifest.expected?.finalLane ?? '6-completed',
      passed: currentTask.state === (manifest.expected?.finalLane ?? '6-completed')
    },
    lease: {
      runAttemptId: claim.run.runId,
      fence: claim.lease.fence,
      status: leaseStatus,
      runStatus: attempts[0]?.run?.status ?? null,
      attempts: attempts.length,
      noDuplicateClaim: attempts.length === 1
    },
    process: {
      active: processEvidence.filter(item => !item.reaped).length,
      observations: processEvidence,
      allReaped: processEvidence.every(item => item.reaped)
    },
    worktree: worktreeEvidence,
    outbox: {
      logicalItems: logicalOutbox.length,
      acknowledgedItems: acknowledged.size,
      backlog: logicalOutbox.filter(row => !acknowledged.has(row.sequence)).length,
      monotonic: logicalOutbox.every((row, index) =>
        index === 0 || row.sequence > logicalOutbox[index - 1].sequence),
      serverEvents: history.events.length,
      serverArtifacts: history.artifacts.length,
      terminalFactCopies: history.events.filter(
        event => event.idempotencyKey === `${runId}:terminal-fact`).length,
      terminalArtifactCopies: history.artifacts.filter(
        artifact => artifact.idempotencyKey === `${runId}:terminal-evidence`).length,
      apiAttempts: api.snapshot()
    },
    sha: {
      resultSha,
      handoffResultSha: handoff?.envelope?.resultSha ?? null,
      originHead,
      integrated: resultIntegrated,
      phantomSuccessPrevented: accepted ? resultIntegrated : !resultIntegrated
    },
    incident: {
      selectedFaults: faults.ids,
      outcome: incidentOutcome,
      expected: manifest.expected?.incidentOutcome ?? 'none',
      evidence: incidentEvidence
    }
  };
  assertScenario(manifest, {
    accepted,
    phaseSequence: phaseEvents.filter(event => event.point === 'after')
      .map(event => event.phase),
    assertions
  });
  const report = {
    scenario: manifest.name,
    seed,
    runId,
    accepted,
    resultSha,
    semanticTree,
    phaseSequence: phaseEvents.filter(event => event.point === 'after')
      .map(event => event.phase),
    incidentOutcome,
    assertions,
    faultSchedule: faults.snapshot(),
    resourcesRoot: root
  };
  await writeFile(
    path.join(root, 'assertions.json'),
    `${JSON.stringify(report, null, 2)}\n`);
  return report;
}

async function readJsonl(file) {
  if (!await exists(file)) return [];
  const content = (await readFile(file, 'utf8')).trim();
  return content ? content.split(/\r?\n/).map(JSON.parse) : [];
}

function assertScenario(manifest, { accepted, phaseSequence, assertions }) {
  const expected = manifest.expected ?? {
    accepted: true,
    finalLane: '6-completed',
    incidentOutcome: 'none',
    phaseSequence: expectedPhases
  };
  const failures = [];
  if (accepted !== expected.accepted) {
    failures.push(`accepted expected ${expected.accepted}, got ${accepted}`);
  }
  if (JSON.stringify(phaseSequence) !== JSON.stringify(expected.phaseSequence)) {
    failures.push(
      `phase sequence expected ${expected.phaseSequence.join(',')}, got ${phaseSequence.join(',')}`);
  }
  if (!assertions.lane.passed) {
    failures.push(
      `lane expected ${assertions.lane.expected}, got ${assertions.lane.actual}`);
  }
  if (assertions.incident.outcome !== expected.incidentOutcome) {
    failures.push(
      `incident expected ${expected.incidentOutcome}, got ${assertions.incident.outcome}`);
  }
  if (!assertions.lease.noDuplicateClaim) failures.push('claim produced duplicate RunAttempts');
  if (!assertions.process.allReaped) failures.push('a fault-owned process survived cleanup');
  if (assertions.outbox.backlog !== 0) failures.push('durable outbox retained unacknowledged evidence');
  if (!assertions.outbox.monotonic) failures.push('durable outbox sequence is not monotonic');
  if (assertions.outbox.terminalFactCopies > 1) failures.push('terminal fact replay was duplicated');
  if (assertions.outbox.terminalArtifactCopies > 1) failures.push('terminal artifact replay was duplicated');
  if (assertions.incident.selectedFaults.includes('task-server-network-blips')) {
    for (const operation of [
      'claim',
      'heartbeat',
      'event-report',
      'artifact-report',
      'completion-report'
    ]) {
      if (assertions.outbox.apiAttempts[operation] !== 2) {
        failures.push(`${operation} did not perform exactly one bounded replay`);
      }
    }
    if (assertions.outbox.terminalFactCopies !== 1) {
      failures.push('network replay did not persist exactly one terminal fact');
    }
    if (assertions.outbox.terminalArtifactCopies !== 1) {
      failures.push('network replay did not persist exactly one terminal artifact');
    }
  }
  if (assertions.incident.selectedFaults.includes('worktree-target-collision')) {
    if (assertions.worktree.prepareAttempts !== 5) {
      failures.push('worktree collision did not exhaust the five-attempt bound');
    }
    if (!assertions.worktree.foreignPreserved) {
      failures.push('worktree collision modified or deleted the foreign occupant');
    }
    if (!assertions.worktree.foreignRegistered) {
      failures.push('worktree collision lost the foreign Git worktree registration');
    }
  }
  if (assertions.incident.selectedFaults.includes('gate-watchdog-timeout')
      && (assertions.process.observations.length !== 1
          || !assertions.process.observations[0].reaped)) {
    failures.push('gate timeout did not prove full process-tree reaping');
  }
  if (!assertions.sha.phantomSuccessPrevented) failures.push('Result-SHA evidence permitted phantom success');
  if (failures.length) {
    throw new Error(`Scenario assertions failed:\n- ${failures.join('\n- ')}`);
  }
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

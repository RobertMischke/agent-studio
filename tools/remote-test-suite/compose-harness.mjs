#!/usr/bin/env node
import { randomBytes } from 'node:crypto';
import { spawn } from 'node:child_process';
import {
  chmod,
  copyFile,
  mkdir,
  readFile,
  rm,
  stat,
  writeFile
} from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  assertRollingEvidence,
  composeCommand,
  createComposePlan,
  defaultPorts,
  redact,
  unitOperationPlan
} from './compose-core.mjs';

const suiteRoot = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(suiteRoot, '..', '..');
const args = parseArgs(process.argv.slice(2));
const plan = createComposePlan({ repoRoot, runId: args.runId, ports: args.ports });

if (args.command === 'inspect') {
  console.log(JSON.stringify({
    dryRun: true,
    ...plan,
    acceptanceSequence: [
      'provision',
      'reference-task',
      'studio-partition-and-replacement',
      'runner-partition-and-replacement',
      'task-server-partition-and-honest-fence-recovery',
      'evidence-export',
      'identity-scoped-teardown'
    ],
    operation: args.operation && args.unit
      ? unitOperationPlan(args.operation, args.unit, { force: args.force })
      : null
  }, null, 2));
  process.exit(0);
}

let authToken = null;
let stackStarted = false;
let result;
try {
  if (args.command === 'run' || args.command === 'up') {
    await initializeRunRoot();
    await ensureNoIdentityResources();
    await validateCompose();
    stackStarted = true;
    await compose(['up', '--build', '--detach', '--wait', '--wait-timeout', '180'], 1_200_000);
    await waitForJson(`${plan.urls.taskServer}/readyz`, value => value.status === 'ready', 90_000);
    await waitForJson(`${plan.urls.runnerControl}/healthz`, value => value.status === 'ready', 60_000);
    await waitForText(`${plan.urls.studio}/`, text => text.includes('<app-root'), 60_000);
  } else {
    await loadExistingEnvironment();
    stackStarted = await hasIdentityContainers();
  }

  if (args.command === 'up') {
    result = { status: 'ready', project: plan.project, urls: plan.urls, root: plan.root };
  } else if (args.command === 'down') {
    await collectEvidence('manual-down');
    await executeUnitOperation('stop', 'task-server', args.force);
    await teardown();
    stackStarted = false;
    result = { status: 'removed', project: plan.project, evidenceRoot: plan.evidenceRoot };
  } else if (args.command === 'control') {
    await executeUnitOperation(args.operation, args.unit, args.force);
    result = {
      status: 'completed',
      operation: args.operation,
      unit: args.unit,
      project: plan.project
    };
  } else if (args.command === 'run') {
    result = await runAcceptance();
  }
} catch (error) {
  await writeFailure(error).catch(() => {});
  throw error;
} finally {
  if (args.command === 'run' && stackStarted && !args.keep) {
    await collectEvidence('final').catch(error => progress(`evidence collection failed: ${error.message}`));
    await teardown();
    stackStarted = false;
  }
}

console.log(JSON.stringify(result, null, 2));

async function runAcceptance() {
  progress('capturing component and container versions');
  const versions = await captureVersions();
  await writeEvidence('versions.json', versions);

  progress('running deterministic reference task through the isolated Task Server');
  const reference = await runReferenceScenario();

  progress('seeding an active rolling-update task');
  const rollIds = {
    workspace: `wsp-roll-${args.runId}`,
    project: `prj-roll-${args.runId}`,
    task: `tsk-roll-${args.runId}`
  };
  await api('/api/v1/workspaces', {
    method: 'POST',
    body: { name: `Compose rolling ${args.runId}`, workspaceId: rollIds.workspace }
  });
  await api('/api/v1/projects', {
    method: 'POST',
    body: {
      workspaceId: rollIds.workspace,
      name: `Compose rolling ${args.runId}`,
      taskKeyPrefix: 'ROLL',
      projectId: rollIds.project
    }
  });
  const rollingTask = await api(`/api/v1/projects/${rollIds.project}/tasks`, {
    method: 'POST',
    body: {
      title: 'Hold deterministic infrastructure authority',
      body: 'Infrastructure-only lease used by the remote Compose harness.',
      state: '2-ready',
      taskId: rollIds.task,
      taskKey: 'ROLL-1'
    }
  });
  const claimed = await runner('/claim', { method: 'POST' });
  const originalAuthority = structuredClone(claimed);
  await waitForRunner(value => value.renewals >= 1, 20_000);
  const active = await history(rollIds.project, rollingTask.taskId);

  progress('partitioning Studio without transferring execution ownership');
  const renewalBeforeStudioPartition = (await runner('/status')).renewals;
  await proxy('studio', 'partition');
  const studioShellDuringPartition = await fetch(`${plan.urls.studio}/`);
  if (!studioShellDuringPartition.ok) throw new Error('Studio static shell stopped during its Task Server partition.');
  const partitionedStudioApi = await fetch(`${plan.urls.studio}/api/v1/management/status`);
  if (partitionedStudioApi.ok) throw new Error('Studio API unexpectedly remained connected during partition.');
  await waitForRunner(value => value.renewals > renewalBeforeStudioPartition, 10_000);
  await proxy('studio', 'heal');
  await waitForJson(`${plan.urls.studio}/api/v1/management/status`, value => value.authorityReady === true, 20_000);

  progress('replacing Studio while the Runner keeps the active lease');
  const studioBefore = await containerId('studio');
  await executeUnitOperation('replace', 'studio');
  const studioAfter = await containerId('studio');
  assertChangedContainer('Studio', studioBefore, studioAfter);
  await waitForText(`${plan.urls.studio}/`, text => text.includes('<app-root'), 30_000);
  const afterStudio = await historyViaStudio(rollIds.project, rollingTask.taskId);

  progress('partitioning and healing the Runner transport');
  const runnerBeforePartition = await runner('/status');
  await proxy('runner', 'partition');
  await waitForRunner(value => value.renewalFailures > runnerBeforePartition.renewalFailures, 12_000);
  await proxy('runner', 'heal');
  await waitForRunner(value => value.renewals > runnerBeforePartition.renewals, 15_000);

  progress('partitioning and healing Task Server client links');
  const beforeServerPartition = await runner('/status');
  await executeUnitOperation('partition', 'task-server');
  await waitForRunner(value => value.renewalFailures > beforeServerPartition.renewalFailures, 12_000);
  const taskServerPartitionedStudio = await fetch(`${plan.urls.studio}/api/v1/management/status`);
  if (taskServerPartitionedStudio.ok) throw new Error('Studio reached Task Server while the Task Server links were partitioned.');
  await executeUnitOperation('heal', 'task-server');
  await waitForRunner(value => value.renewals > beforeServerPartition.renewals, 15_000);

  progress('replacing the Runner and reattaching persisted attempt authority');
  const runnerBefore = await containerId('agent-runner');
  await executeUnitOperation('replace', 'runner');
  const runnerAfter = await containerId('agent-runner');
  assertChangedContainer('Runner', runnerBefore, runnerAfter);
  await waitForRunner(value => value.claim?.run?.runId === originalAuthority.run.runId
    && value.claim?.lease?.fence === originalAuthority.lease.fence
    && value.renewals >= 1, 20_000);
  const afterRunner = await history(rollIds.project, rollingTask.taskId);

  progress('draining and replacing Task Server with active authority');
  const draining = await api('/api/v1/management/mode', {
    method: 'PUT',
    body: { mode: 1, reason: 'remote Compose harness Task Server rolling update' }
  });
  const deferredShutdown = await api('/api/v1/management/prepare-shutdown', {
    method: 'POST',
    body: { reason: 'remote Compose harness active-authority update proof' }
  });
  if (deferredShutdown.safeToStop || deferredShutdown.unresolvedAttempts !== 1) {
    throw new Error('Task Server did not defer shutdown for active authority.');
  }
  const taskServerBefore = await containerId('task-server');
  await executeUnitOperation('replace', 'task-server', true);
  const taskServerAfter = await containerId('task-server');
  assertChangedContainer('Task Server', taskServerBefore, taskServerAfter);
  await waitForJson(`${plan.urls.taskServer}/readyz`, value => value.status === 'ready', 60_000);
  await api('/api/v1/management/mode', {
    method: 'PUT',
    body: { mode: 0, reason: 'remote Compose harness recovery inspection' }
  });
  const quarantined = await history(rollIds.project, rollingTask.taskId);
  if (!quarantined.runs.some(run => run.status === 'process-unknown')) {
    throw new Error('Task Server restart did not quarantine active authority.');
  }

  progress('proving old Runner containment ended and recovering with a higher fence');
  const staleRunnerContainer = await containerId('agent-runner');
  await executeUnitOperation('replace', 'runner');
  const recoveryRunnerContainer = await containerId('agent-runner');
  assertChangedContainer('Recovery Runner', staleRunnerContainer, recoveryRunnerContainer);
  const unknownRun = quarantined.runs.find(run => run.status === 'process-unknown');
  await api(`/api/v1/management/attempts/${unknownRun.runId}/resolve-unknown`, {
    method: 'POST',
    body: {
      containmentProof: `Compose removed container ${staleRunnerContainer}; replacement ${recoveryRunnerContainer} has a distinct container identity`,
      resolution: 'requeue'
    }
  });
  await runner('/forget', { method: 'POST' });
  const replacementClaim = await runner('/claim', { method: 'POST' });
  if (replacementClaim.run.runId === originalAuthority.run.runId
      || replacementClaim.lease.fence <= originalAuthority.lease.fence) {
    throw new Error('Recovered attempt did not receive a distinct run and higher fence.');
  }

  const staleWrite = await apiRaw(`/api/v1/runs/${originalAuthority.run.runId}/completion`, {
    method: 'POST',
    body: {
      runnerId: originalAuthority.lease.runnerId,
      instanceId: originalAuthority.lease.instanceId,
      leaseId: originalAuthority.lease.leaseId,
      fence: originalAuthority.lease.fence,
      outcome: 'Done',
      summary: 'This stale completion must be rejected.',
      idempotencyKey: `${args.runId}:stale-completion`,
      sequence: 1
    }
  });
  if (staleWrite.status !== 409) {
    throw new Error(`Stale completion returned ${staleWrite.status}, expected 409.`);
  }
  await runner('/release', { method: 'POST' });
  const finalHistory = await history(rollIds.project, rollingTask.taskId);

  progress('asserting empty authority and delivery backlogs');
  await api('/api/v1/management/mode', {
    method: 'PUT',
    body: { mode: 1, reason: 'remote Compose harness final authority check' }
  });
  const finalShutdown = await api('/api/v1/management/prepare-shutdown', {
    method: 'POST',
    body: { reason: 'remote Compose harness final authority check' }
  });
  const status = await api('/api/v1/management/status');
  const outboxes = await api('/api/v1/management/outboxes');
  const audit = await api('/api/v1/management/audit');
  const invariants = await api('/api/v1/management/invariants');
  const evidence = {
    reference,
    active,
    afterStudio,
    afterRunner,
    draining,
    deferredShutdown,
    quarantined,
    staleWriteStatus: staleWrite.status,
    finalHistory,
    finalShutdown,
    status,
    outboxes,
    audit,
    invariants
  };
  const assertions = assertRollingEvidence(evidence);
  const acceptance = {
    schemaVersion: 1,
    runId: args.runId,
    project: plan.project,
    completedAt: new Date().toISOString(),
    assertions,
    reference,
    rollingTask: {
      projectId: rollIds.project,
      taskId: rollingTask.taskId,
      originalRunId: originalAuthority.run.runId,
      recoveredRunId: replacementClaim.run.runId,
      originalFence: originalAuthority.lease.fence,
      recoveredFence: replacementClaim.lease.fence,
      finalState: finalHistory.task.state
    },
    taskServerUpdate: {
      oldContainerId: taskServerBefore,
      newContainerId: taskServerAfter,
      deferredShutdown,
      recovery: 'process-unknown fenced after positive container replacement proof'
    },
    evidenceRoot: plan.evidenceRoot
  };
  await writeEvidence('acceptance.json', acceptance);
  await writeEvidence('api/status.json', status);
  await writeEvidence('api/audit.json', audit);
  await writeEvidence('api/invariants.json', invariants);
  await writeEvidence('api/outboxes.json', outboxes);
  await writeEvidence('api/rolling-history.json', finalHistory);
  return acceptance;
}

async function runReferenceScenario() {
  const execution = await execute([
    'node',
    path.join(suiteRoot, 'index.mjs'),
    '--scenario', 'reference-change',
    '--seed', 'compose-reference-v1',
    '--run-id', `${args.runId}-reference`,
    '--root', path.join(plan.root, 'scenarios'),
    '--server-url', plan.urls.taskServer,
    '--auth-token-file', plan.tokenFile
  ], { cwd: repoRoot, timeoutMs: 300_000 });
  const parsed = JSON.parse(execution.stdout);
  await writeEvidence('reference-task.json', parsed);
  return parsed;
}

async function captureVersions() {
  const [docker, composeVersion, server, runnerVersion, studio, nodeVersion, revision] = await Promise.all([
    execute(['docker', 'version', '--format', '{{json .}}'], { timeoutMs: 30_000 }),
    execute(['docker', 'compose', 'version', '--short'], { timeoutMs: 30_000 }),
    composeExec('task-server', ['dotnet', 'task-server.dll', '--version']),
    composeExec('agent-runner', ['dotnet', '/opt/agent-host/agent-host.dll', '--version']),
    composeExec('studio', ['caddy', 'version']),
    composeExec('fault-proxy', ['node', '--version']),
    execute(['git', 'rev-parse', 'HEAD'], { cwd: repoRoot, timeoutMs: 10_000 })
  ]);
  const images = {};
  for (const service of ['task-server', 'fault-proxy', 'agent-runner', 'studio']) {
    const imageId = (await compose(['images', '--quiet', service], 30_000)).stdout.trim();
    const inspected = await execute([
      'docker', 'image', 'inspect', imageId,
      '--format', '{{json .Id}}|{{json .RepoDigests}}|{{json .Config.Labels}}'
    ], { timeoutMs: 30_000 });
    images[service] = inspected.stdout.trim();
  }
  return {
    capturedAt: new Date().toISOString(),
    repositoryRevision: revision.stdout.trim(),
    docker: JSON.parse(docker.stdout),
    compose: composeVersion.stdout.trim(),
    components: {
      taskServer: server.stdout.trim(),
      agentRunner: runnerVersion.stdout.trim(),
      studio: studio.stdout.trim(),
      faultProxyRuntime: nodeVersion.stdout.trim()
    },
    images
  };
}

async function executeUnitOperation(operation, unit, force = false) {
  const steps = unitOperationPlan(operation, unit, { force });
  for (const step of steps) {
    if (step.kind === 'proxy') {
      const response = await fetch(`${plan.urls.faultControl}${step.route}`, { method: step.method });
      if (!response.ok) throw new Error(`${operation} ${unit} failed at ${step.route}: ${response.status}`);
    } else if (step.kind === 'api') {
      await api(step.route, { method: step.method, body: step.body });
    } else if (step.kind === 'prepare-shutdown') {
      const prepared = await api(step.route, { method: step.method, body: step.body });
      if (!prepared.safeToStop && !step.allowUnsafe) {
        throw new Error(`Task Server refused bounded shutdown: ${prepared.message}`);
      }
    } else if (step.kind === 'wait-ready') {
      await waitForJson(`${plan.urls.taskServer}/readyz`, value => value.status === 'ready', 60_000);
    } else if (step.kind === 'wait-unit') {
      if (step.unit === 'runner') {
        await waitForJson(`${plan.urls.runnerControl}/healthz`, value => value.status === 'ready', 60_000);
      } else if (step.unit === 'studio') {
        await waitForText(`${plan.urls.studio}/`, text => text.includes('<app-root'), 60_000);
      }
    } else if (step.kind === 'compose') {
      await compose(step.args, 180_000);
    }
  }
}

async function initializeRunRoot() {
  if (await exists(plan.root)) {
    throw new Error(`Run id '${args.runId}' already has a workspace or evidence. Use a new run id.`);
  }
  await mkdir(plan.evidenceRoot, { recursive: true });
  authToken = randomBytes(36).toString('base64url');
  const revision = (await execute(['git', 'rev-parse', 'HEAD'], { cwd: repoRoot, timeoutMs: 10_000 })).stdout.trim();
  const environment = [
    `REMOTE_HARNESS_PROJECT=${plan.project}`,
    `REMOTE_HARNESS_RUN_ID=${plan.runId}`,
    `REMOTE_HARNESS_IMAGE_TAG=local`,
    `REMOTE_HARNESS_REVISION=${revision}`,
    `REMOTE_HARNESS_AUTH_TOKEN=${authToken}`,
    `REMOTE_HARNESS_TASK_SERVER_PORT=${plan.ports.taskServer}`,
    `REMOTE_HARNESS_STUDIO_PORT=${plan.ports.studio}`,
    `REMOTE_HARNESS_FAULT_CONTROL_PORT=${plan.ports.faultControl}`,
    `REMOTE_HARNESS_RUNNER_CONTROL_PORT=${plan.ports.runnerControl}`
  ].join('\n') + '\n';
  await writeFile(plan.environmentFile, environment, { mode: 0o600 });
  await writeFile(plan.tokenFile, `${authToken}\n`, { mode: 0o600 });
  await chmod(plan.environmentFile, 0o600);
  await chmod(plan.tokenFile, 0o600);
  await writeEvidence('resource-plan.json', {
    ...plan,
    environmentFile: path.relative(plan.repoRoot, plan.environmentFile),
    tokenFile: '[ephemeral credential removed during teardown]'
  });
  await copyFile(plan.composeFile, path.join(plan.evidenceRoot, 'compose-source.yaml'));
}

async function loadExistingEnvironment() {
  const contents = await readFile(plan.environmentFile, 'utf8');
  const environment = Object.fromEntries(contents.trim().split(/\r?\n/)
    .map(line => line.split(/=(.*)/s).slice(0, 2)));
  if (environment.REMOTE_HARNESS_PROJECT !== plan.project
      || environment.REMOTE_HARNESS_RUN_ID !== plan.runId) {
    throw new Error('Existing harness environment identity does not match the requested run.');
  }
  authToken = environment.REMOTE_HARNESS_AUTH_TOKEN;
  if (!authToken) throw new Error('Existing harness environment has no authentication token.');
}

async function validateCompose() {
  await compose(['config', '--quiet'], 30_000);
  const services = (await compose(['config', '--services'], 30_000)).stdout.trim().split(/\r?\n/).sort();
  const expected = ['agent-runner', 'fault-proxy', 'studio', 'task-server'];
  if (JSON.stringify(services) !== JSON.stringify(expected)) {
    throw new Error(`Compose profile resolved unexpected services: ${services.join(', ')}`);
  }
}

async function collectEvidence(label) {
  if (!await exists(plan.environmentFile)) return;
  await mkdir(plan.evidenceRoot, { recursive: true });
  const ps = await composeAllowFailure(['ps', '--all', '--format', 'json'], 30_000);
  await writeTextEvidence(`compose-ps-${label}.json`, ps.stdout || ps.stderr);
  for (const service of ['task-server', 'fault-proxy', 'agent-runner', 'studio']) {
    const logs = await composeAllowFailure(['logs', '--no-color', '--timestamps', service], 30_000);
    await writeTextEvidence(`logs/${service}.log`, redact(logs.stdout + logs.stderr, [authToken]));
    const id = (await composeAllowFailure(['ps', '--all', '--quiet', service], 15_000)).stdout.trim();
    if (id) {
      const inspected = await executeAllowFailure(['docker', 'inspect', id], { timeoutMs: 30_000 });
      await writeTextEvidence(`inspect/${service}.json`, redact(inspected.stdout + inspected.stderr, [authToken]));
    }
  }
  for (const [file, route] of Object.entries({
    status: '/api/v1/management/status',
    audit: '/api/v1/management/audit',
    invariants: '/api/v1/management/invariants',
    outboxes: '/api/v1/management/outboxes'
  })) {
    const response = await apiRaw(route);
    if (response.status === 200) await writeEvidence(`api/${file}.json`, response.value);
  }
}

async function teardown() {
  if (!await exists(plan.environmentFile)) return;
  await assertOwnedResources();
  await composeAllowFailure([
    'down', '--volumes', '--remove-orphans', '--rmi', 'all', '--timeout', '20'
  ], 120_000);
  const residues = await identityResidues();
  if (residues.containers.length || residues.volumes.length || residues.networks.length) {
    throw new Error(`Identity-scoped teardown left resources: ${JSON.stringify(residues)}`);
  }
  await rm(plan.environmentFile, { force: true });
  await rm(plan.tokenFile, { force: true });
  await writeEvidence('teardown.json', {
    completedAt: new Date().toISOString(),
    project: plan.project,
    identityLabel: plan.identityLabel,
    residues
  });
}

async function assertOwnedResources() {
  const ids = (await executeAllowFailure([
    'docker', 'ps', '--all', '--quiet',
    '--filter', `label=com.docker.compose.project=${plan.project}`
  ], { timeoutMs: 30_000 })).stdout.trim().split(/\r?\n/).filter(Boolean);
  for (const id of ids) {
    const label = (await execute([
      'docker', 'inspect', id,
      '--format', '{{index .Config.Labels "com.agentstudio.remote-harness.identity"}}'
    ], { timeoutMs: 30_000 })).stdout.trim();
    if (label !== plan.project) {
      throw new Error(`Refusing teardown: container ${id} lacks harness identity ${plan.project}.`);
    }
  }
  for (const volume of plan.resources.volumes) {
    await assertDockerLabel(
      ['docker', 'volume', 'inspect', volume, '--format',
        '{{index .Labels "com.agentstudio.remote-harness.identity"}}'],
      `volume ${volume}`
    );
  }
  await assertDockerLabel(
    ['docker', 'network', 'inspect', plan.resources.network, '--format',
      '{{index .Labels "com.agentstudio.remote-harness.identity"}}'],
    `network ${plan.resources.network}`
  );
  for (const image of plan.resources.images) {
    await assertDockerLabel(
      ['docker', 'image', 'inspect', image, '--format',
        '{{index .Config.Labels "com.agentstudio.remote-harness.identity"}}'],
      `image ${image}`
    );
  }
}

async function assertDockerLabel(command, description) {
  const inspected = await executeAllowFailure(command, { timeoutMs: 30_000 });
  if (inspected.code !== 0) return;
  if (inspected.stdout.trim() !== plan.project) {
    throw new Error(`Refusing teardown: ${description} lacks harness identity ${plan.project}.`);
  }
}

async function ensureNoIdentityResources() {
  const residues = await identityResidues();
  if (residues.containers.length || residues.volumes.length || residues.networks.length) {
    throw new Error(`Harness identity already owns Docker resources: ${JSON.stringify(residues)}`);
  }
  const collisions = await exactNameCollisions();
  if (collisions.length) {
    throw new Error(`Refusing to reuse explicit Docker resource names: ${collisions.join(', ')}`);
  }
}

async function exactNameCollisions() {
  const collisions = [];
  for (const container of plan.resources.containers) {
    const found = await executeAllowFailure([
      'docker', 'ps', '--all', '--quiet', '--filter', `name=^/${container}$`
    ], { timeoutMs: 30_000 });
    if (found.stdout.trim()) collisions.push(`container:${container}`);
  }
  for (const volume of plan.resources.volumes) {
    const found = await executeAllowFailure(['docker', 'volume', 'inspect', volume], { timeoutMs: 30_000 });
    if (found.code === 0) collisions.push(`volume:${volume}`);
  }
  const network = await executeAllowFailure(
    ['docker', 'network', 'inspect', plan.resources.network],
    { timeoutMs: 30_000 }
  );
  if (network.code === 0) collisions.push(`network:${plan.resources.network}`);
  for (const image of plan.resources.images) {
    const found = await executeAllowFailure(['docker', 'image', 'inspect', image], { timeoutMs: 30_000 });
    if (found.code === 0) collisions.push(`image:${image}`);
  }
  return collisions;
}

async function identityResidues() {
  const label = `com.agentstudio.remote-harness.identity=${plan.project}`;
  const [containers, volumes, networks] = await Promise.all([
    executeAllowFailure(['docker', 'ps', '--all', '--quiet', '--filter', `label=${label}`], { timeoutMs: 30_000 }),
    executeAllowFailure(['docker', 'volume', 'ls', '--quiet', '--filter', `label=${label}`], { timeoutMs: 30_000 }),
    executeAllowFailure(['docker', 'network', 'ls', '--quiet', '--filter', `label=${label}`], { timeoutMs: 30_000 })
  ]);
  return {
    containers: lines(containers.stdout),
    volumes: lines(volumes.stdout),
    networks: lines(networks.stdout)
  };
}

async function hasIdentityContainers() {
  return (await identityResidues()).containers.length > 0;
}

async function api(route, options = {}) {
  const response = await apiRaw(route, options);
  if (response.status < 200 || response.status >= 300) {
    throw new Error(`${options.method ?? 'GET'} ${route} failed (${response.status}): ${JSON.stringify(response.value)}`);
  }
  return response.value;
}

async function apiRaw(route, { method = 'GET', body } = {}) {
  try {
    const response = await fetch(`${plan.urls.taskServer}${route}`, {
      method,
      headers: {
        Authorization: `Bearer ${authToken}`,
        'Content-Type': 'application/json',
        'X-Actor-Id': `remote-compose-harness:${args.runId}`,
        'X-Client-Id': `remote-compose-harness:${args.runId}`,
        'X-Task-Protocol-Version': '2',
        'X-Task-Client-Version': 'remote-compose-harness/1'
      },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    let value;
    try {
      value = text ? JSON.parse(text) : null;
    } catch {
      value = { raw: text };
    }
    return { status: response.status, value };
  } catch (error) {
    return { status: 0, value: { error: String(error?.message ?? error) } };
  }
}

async function runner(route, { method = 'GET' } = {}) {
  const response = await fetch(`${plan.urls.runnerControl}${route}`, { method });
  const value = await response.json();
  if (!response.ok) throw new Error(`Runner control ${route} failed (${response.status}): ${JSON.stringify(value)}`);
  return value;
}

async function proxy(link, action) {
  const response = await fetch(`${plan.urls.faultControl}/links/${link}/${action}`, { method: 'POST' });
  if (!response.ok) throw new Error(`Fault proxy ${link}/${action} failed (${response.status}).`);
  return await response.json();
}

async function history(projectId, taskId) {
  return await api(`/api/v1/projects/${projectId}/tasks/${taskId}/history`);
}

async function historyViaStudio(projectId, taskId) {
  const response = await fetch(`${plan.urls.studio}/api/v1/projects/${projectId}/tasks/${taskId}/history`, {
    headers: {
      'X-Actor-Id': `remote-compose-harness:${args.runId}`,
      'X-Client-Id': `remote-compose-harness:${args.runId}`
    }
  });
  if (!response.ok) throw new Error(`Studio history replay failed (${response.status}).`);
  return await response.json();
}

async function waitForRunner(predicate, timeoutMs) {
  return await waitForJson(`${plan.urls.runnerControl}/status`, predicate, timeoutMs);
}

async function waitForJson(url, predicate, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let last = 'no response';
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      const text = await response.text();
      last = `${response.status}: ${text}`;
      if (response.ok) {
        const value = JSON.parse(text);
        if (predicate(value)) return value;
      }
    } catch (error) {
      last = String(error?.message ?? error);
    }
    await delay(250);
  }
  throw new Error(`Timed out waiting for ${url}: ${last}`);
}

async function waitForText(url, predicate, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let last = 'no response';
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      const text = await response.text();
      last = `${response.status}: ${text.slice(0, 200)}`;
      if (response.ok && predicate(text)) return text;
    } catch (error) {
      last = String(error?.message ?? error);
    }
    await delay(250);
  }
  throw new Error(`Timed out waiting for ${url}: ${last}`);
}

async function containerId(service) {
  const id = (await compose(['ps', '--quiet', service], 30_000)).stdout.trim();
  if (!id) throw new Error(`Compose service ${service} has no running container.`);
  return id;
}

function assertChangedContainer(label, before, after) {
  if (!before || !after || before === after) throw new Error(`${label} replacement did not change container identity.`);
}

async function compose(args, timeoutMs) {
  return await execute(composeCommand(plan, ...args), { cwd: repoRoot, timeoutMs });
}

async function composeAllowFailure(args, timeoutMs) {
  return await executeAllowFailure(composeCommand(plan, ...args), { cwd: repoRoot, timeoutMs });
}

async function composeExec(service, command) {
  return await compose(['exec', '--no-TTY', service, ...command], 30_000);
}

async function execute(command, { cwd = repoRoot, timeoutMs = 60_000 } = {}) {
  const result = await executeAllowFailure(command, { cwd, timeoutMs });
  if (result.code !== 0) {
    throw new Error(`${command.join(' ')} failed (${result.code}): ${result.stderr || result.stdout}`);
  }
  return result;
}

async function executeAllowFailure(command, { cwd = repoRoot, timeoutMs = 60_000 } = {}) {
  return await new Promise((resolve, reject) => {
    const child = spawn(command[0], command.slice(1), {
      cwd,
      env: process.env,
      stdio: ['ignore', 'pipe', 'pipe']
    });
    let stdout = '';
    let stderr = '';
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill('SIGTERM');
      setTimeout(() => child.kill('SIGKILL'), 5000).unref();
    }, timeoutMs);
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', code => {
      clearTimeout(timer);
      resolve({ code: timedOut ? 124 : (code ?? 1), stdout, stderr });
    });
  });
}

async function writeEvidence(relative, value) {
  const file = path.join(plan.evidenceRoot, relative);
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, `${JSON.stringify(value, null, 2)}\n`);
}

async function writeTextEvidence(relative, value) {
  const file = path.join(plan.evidenceRoot, relative);
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, value);
}

async function writeFailure(error) {
  await mkdir(plan.evidenceRoot, { recursive: true });
  await writeEvidence('failure.json', {
    failedAt: new Date().toISOString(),
    message: error?.message ?? String(error),
    stack: redact(error?.stack ?? '', [authToken])
  });
  if (stackStarted) await collectEvidence('failure');
}

async function exists(file) {
  try {
    await stat(file);
    return true;
  } catch (error) {
    if (error?.code === 'ENOENT') return false;
    throw error;
  }
}

function lines(value) {
  return value.trim().split(/\r?\n/).filter(Boolean);
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function progress(message) {
  process.stderr.write(`[remote-compose-harness] ${message}\n`);
}

function parseArgs(values) {
  const result = {
    command: values[0] ?? '',
    runId: '',
    operation: null,
    unit: null,
    force: false,
    keep: false,
    ports: { ...defaultPorts }
  };
  let index = 1;
  if (result.command === 'control') {
    result.operation = values[index++];
    result.unit = values[index++];
  }
  for (; index < values.length; index++) {
    const key = values[index];
    if (key === '--force') result.force = true;
    else if (key === '--keep') result.keep = true;
    else if (key === '--run-id') result.runId = values[++index];
    else if (key === '--task-server-port') result.ports.taskServer = Number(values[++index]);
    else if (key === '--studio-port') result.ports.studio = Number(values[++index]);
    else if (key === '--fault-control-port') result.ports.faultControl = Number(values[++index]);
    else if (key === '--runner-control-port') result.ports.runnerControl = Number(values[++index]);
    else throw new Error(`Unknown argument: ${key}`);
  }
  if (!['inspect', 'run', 'up', 'down', 'control'].includes(result.command) || !result.runId) {
    throw new Error(
      'Usage: compose-harness.mjs inspect|run|up|down --run-id ID, or compose-harness.mjs control stop|restart|replace|partition|heal studio|task-server|runner --run-id ID'
    );
  }
  if (result.command === 'control' && (!result.operation || !result.unit)) {
    throw new Error('Control requires an operation and unit.');
  }
  return result;
}

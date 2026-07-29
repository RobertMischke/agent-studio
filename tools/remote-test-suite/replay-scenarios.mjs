import { spawn } from 'node:child_process';
import {
  chmod,
  mkdir,
  readFile,
  readdir,
  readlink,
  writeFile
} from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import {
  interpolate,
  runCommand,
  scenarioAssertions,
  sha256
} from './core.mjs';

export async function executeHistoricalReplay(context) {
  switch (context.manifest.name) {
    case 'divergent-salvage-lineage':
      return await divergentSalvageLineage(context);
    case 'lease-adoption-restart':
      return await leaseAdoptionRestart(context);
    case 'external-completion-cycle':
      return await externalCompletionCycle(context);
    default:
      throw new Error(`No historical replay executor is registered for '${context.manifest.name}'.`);
  }
}

async function provision(context) {
  const { api, manifest, runId, seed, suiteRoot, repoRoot } = context;
  const variables = { suiteRoot, repoRoot, seed, runId };
  const ids = Object.fromEntries(Object.entries(manifest.resources)
    .map(([key, value]) => [key, interpolate(value, variables)]));
  await api.post('/api/v1/workspaces', {
    name: `Remote Test ${runId}`,
    workspaceId: ids.workspaceId
  });
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
  return { ids, task, variables };
}

async function claimCoding(context, suffix = 'coding') {
  const runnerId = `${suffix}-${context.runId}`;
  const instanceId = `${suffix}-instance-${context.runId}`;
  await context.register(context.api, runnerId, instanceId, `${suffix}-host`, ['coding-executor']);
  const claim = await context.api.post(`/api/v1/runners/${runnerId}/claims`, {
    runnerId,
    instanceId,
    requestedTtlSeconds: 120,
    availableSlots: 1
  });
  if (claim.status !== 'claimed') {
    throw new Error(`Historical replay task was not claimed: ${JSON.stringify(claim)}`);
  }
  return { claim, runnerId, instanceId };
}

async function divergentSalvageLineage(context) {
  const { manifest, root, seed, runId, api, hook } = context;
  const checks = scenarioAssertions(manifest);
  const { ids, task, variables } = await provision(context);
  const origin = path.join(root, 'fixture-origin.git');

  await hook('claim', 'before');
  const coding = await claimCoding(context);
  await hook('claim', 'after', {
    taskKey: coding.claim.task.taskKey,
    runAttemptId: coding.claim.run.runId,
    fence: coding.claim.lease.fence
  });

  await hook('run', 'before');
  const repo = path.join(root, 'lineage', 'repo');
  await mkdir(path.dirname(repo), { recursive: true });
  await runCommand(['git', 'clone', origin, repo], { cwd: root });
  await configureGit(repo, 'Lineage Replay');
  const baseSha = (await runCommand(
    ['git', 'rev-parse', `origin/${manifest.fixture.defaultBranch}`],
    { cwd: repo })).stdout.trim();
  const canonicalBranch = `task/${manifest.task.key.toLowerCase()}`;
  await runCommand(['git', 'checkout', '-b', canonicalBranch, baseSha], { cwd: repo });
  await writeFile(
    path.join(repo, 'canonical-stale.txt'),
    `stale canonical scope for ${manifest.task.key}\n`);
  await runCommand(['git', 'add', 'canonical-stale.txt'], { cwd: repo });
  await commit(repo, `chore(${manifest.task.key}): stale canonical placeholder`, seed, 1);
  const canonicalTip = await revParse(repo, 'HEAD');
  await runCommand(['git', 'push', 'origin', `HEAD:refs/heads/${canonicalBranch}`], { cwd: repo });

  const hostBranch = `host-lineage-${manifest.task.key.toLowerCase()}`;
  await runCommand(['git', 'checkout', '-b', hostBranch, baseSha], { cwd: repo });
  await writeFile(path.join(repo, 'foreign-task.txt'), 'foreign task payload\n');
  await runCommand(['git', 'add', 'foreign-task.txt'], { cwd: repo });
  await commit(repo, 'feat(RTS-999): unrelated host-lineage work', seed, 2);
  const foreignCommit = await revParse(repo, 'HEAD');
  await runCommand(
    manifest.fixture.changeCommand.map(value => interpolate(value, variables)),
    { cwd: repo });
  await runCommand(['git', 'add', '.'], { cwd: repo });
  await commit(repo, `feat(${manifest.task.key}): recover priority shipping quotes`, seed, 3);
  const taskCommit = await revParse(repo, 'HEAD');
  const hostTip = taskCommit;
  const collisionRef =
    `refs/heads/runner/${coding.runnerId}/${manifest.task.key.toLowerCase()}-collision-`
    + `${hostTip.slice(0, 12)}-${canonicalTip.slice(0, 12)}`;
  await runCommand(['git', 'push', 'origin', `${hostTip}:${collisionRef}`], { cwd: repo });

  const resultBranch = `reconciled-${manifest.task.key.toLowerCase()}`;
  await runCommand(['git', 'checkout', '-b', resultBranch, baseSha], { cwd: repo });
  await runCommand(['git', 'cherry-pick', taskCommit], { cwd: repo });
  const resultSha = await revParse(repo, 'HEAD');
  const resultRef = `refs/heads/agent-studio/results/${coding.claim.run.runId}/${resultSha}`;
  const deliveryRef = `refs/heads/runner/${coding.runnerId}/${manifest.task.key.toLowerCase()}`;
  await runCommand(
    ['git', 'push', 'origin', `HEAD:${deliveryRef}`, `HEAD:${resultRef}`],
    { cwd: repo });
  const commonBase = (await runCommand(
    ['git', 'merge-base', canonicalTip, hostTip],
    { cwd: repo })).stdout.trim();
  checks.check(
    'divergent-lineage-reconstructed',
    commonBase === baseSha && canonicalTip !== hostTip && resultSha !== hostTip,
    `canonical=${canonicalTip}, host=${hostTip}, immutableBase=${baseSha}, result=${resultSha}`);
  await hook('run', 'after', {
    baseSha,
    canonicalTip,
    hostTip,
    collisionRef,
    resultSha,
    resultRef
  });

  await hook('gate', 'before');
  const collisionTip = (await runCommand(
    ['git', '--git-dir', origin, 'rev-parse', collisionRef],
    { cwd: root })).stdout.trim();
  checks.check(
    'recoverable-host-tip-preserved',
    collisionTip === hostTip,
    `collision ref ${collisionRef} retains ${collisionTip}`);
  const changed = await changedFiles(repo, baseSha, resultSha);
  const expected = [...manifest.fixture.expectedChangedFiles].sort();
  const resultParents = (await runCommand(
    ['git', 'rev-list', '--parents', '-n', '1', resultSha],
    { cwd: repo })).stdout.trim().split(/\s+/);
  const foreignReachable = await containsCommit(repo, baseSha, resultSha, foreignCommit);
  if (JSON.stringify(changed) !== JSON.stringify(expected)) {
    throw new Error(`Reconciled immutable range differs. Expected ${expected}; got ${changed}`);
  }
  checks.check(
    'immutable-range-excludes-foreign-scope',
    resultParents[1] === baseSha && !foreignReachable && !changed.includes('foreign-task.txt'),
    `range ${baseSha}..${resultSha} contains only ${changed.join(', ')}`);
  const gate = await runCommand(manifest.fixture.acceptanceCommand, { cwd: repo });
  await hook('gate', 'after', {
    status: 'pass',
    outputSha256: sha256(gate.stdout + gate.stderr),
    changedFiles: changed
  });

  const result = resultIdentity(origin, coding.claim.run.runId, baseSha, resultSha, resultRef);
  const handoff = handoffRequest(
    coding,
    result,
    `${runId}:lineage-handoff`,
    1);
  await api.put(`/api/v1/runs/${coding.claim.run.runId}/result-handoff`, handoff);
  await api.post(`/api/v1/runs/${coding.claim.run.runId}/completion`, {
    runnerId: coding.runnerId,
    instanceId: coding.instanceId,
    leaseId: coding.claim.lease.leaseId,
    fence: coding.claim.lease.fence,
    outcome: 'Done',
    summary: 'Divergent salvage was reconciled onto the immutable base.',
    resultEnvelopeDigest: result.envelopeDigest,
    idempotencyKey: `${runId}:lineage-completion`,
    sequence: 2
  });

  await hook('review', 'before');
  const subject = await createReviewSubject(
    context,
    task,
    coding.claim.run.runId,
    result,
    `${runId}:lineage-subject`);
  const reviewer = {
    runnerId: `review-${runId}`,
    instanceId: `review-instance-${runId}`,
    hostId: 'review-host'
  };
  await context.register(
    api,
    reviewer.runnerId,
    reviewer.instanceId,
    reviewer.hostId,
    ['review-executor', 'review:git', 'review:semantic']);

  const contaminated = await executeReviewAttempt(
    context,
    reviewer,
    repo,
    result,
    hostTip,
    `${runId}:review-contaminated`,
    false);
  checks.check(
    'cross-contaminated-review-rejected',
    contaminated.report.outcome === 'ReviewInfra'
      && contaminated.report.failureClassification === 'ShaMismatch'
      && contaminated.report.retryScheduled === true,
    `cross-contaminated ${hostTip} classified ${contaminated.report.failureClassification}`);

  const stale = await executeReviewAttempt(
    context,
    reviewer,
    repo,
    result,
    canonicalTip,
    `${runId}:review-stale`,
    false);
  checks.check(
    'stale-canonical-review-rejected',
    stale.report.outcome === 'ReviewInfra'
      && stale.report.failureClassification === 'ShaMismatch'
      && stale.report.retryScheduled === true,
    `stale canonical ${canonicalTip} classified ${stale.report.failureClassification}`);

  const exact = await executeReviewAttempt(
    context,
    reviewer,
    repo,
    result,
    resultSha,
    `${runId}:review-exact`,
    true);
  await api.post(`/api/v1/reviews/attempts/${exact.claim.attempt.attemptId}/cleanup`, {
    executorId: reviewer.runnerId,
    instanceId: reviewer.instanceId,
    leaseId: exact.claim.lease.leaseId,
    fence: exact.claim.lease.fence,
    authorityEpoch: exact.claim.lease.authorityEpoch,
    idempotencyKey: `${runId}:review-exact-cleanup`,
    workspaceRemoved: true
  });
  checks.check(
    'exact-result-review-passed',
    exact.report.outcome === 'Pass'
      && exact.report.taskState === '5-human-review'
      && exact.claim.subject.expectedResultSha === resultSha
      && exact.claim.subject.subjectId === subject.subjectId,
    `subject ${subject.subjectId} accepted exact result ${resultSha}`);
  await hook('review', 'after', {
    subjectId: subject.subjectId,
    rejectedAttempts: [
      contaminated.claim.attempt.attemptId,
      stale.claim.attempt.attemptId
    ],
    acceptedAttempt: exact.claim.attempt.attemptId,
    resultSha
  });

  await hook('integration', 'before');
  const history = await api.get(
    `/api/v1/projects/${ids.projectId}/tasks/${task.taskId}/history`);
  const current = await api.get(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`);
  checks.check(
    'single-coding-attempt-no-reissue',
    history.runs.length === 1
      && !history.events.some(event => event.kind === 'task.reissued')
      && current.state === '5-human-review',
    `codingRuns=${history.runs.length}, taskState=${current.state}, no reissue event`);
  await hook('integration', 'after', {
    status: 'withheld-for-human-review',
    resultSha,
    taskState: current.state
  });

  return finish(
    context,
    checks,
    current.state,
    3,
    {
      baseSha,
      resultSha,
      canonicalTip,
      hostTip,
      collisionRef,
      reviewSubjectId: subject.subjectId
    });
}

async function leaseAdoptionRestart(context) {
  if (process.platform !== 'linux') {
    throw new Error('lease-adoption-restart requires Linux /proc process identity proof.');
  }
  const { manifest, root, runId, repoRoot, serverUrl, api, hook } = context;
  const checks = scenarioAssertions(manifest);
  const { ids, task } = await provision(context);
  const origin = path.join(root, 'fixture-origin.git');
  const control = path.join(root, 'restart-control');
  const runnerWork = path.join(root, 'runner-host');
  const stateDir = path.join(root, 'runner-state');
  await mkdir(control, { recursive: true });
  await mkdir(runnerWork, { recursive: true });
  const fakeCli = path.join(control, 'restart-worker.sh');
  await writeRestartWorker(fakeCli);

  await runCommand(
    ['dotnet', 'build', path.join(repoRoot, 'runner', 'AgentRunner.csproj'), '--nologo', '--verbosity', 'quiet'],
    { cwd: repoRoot });
  const runnerDll = path.join(repoRoot, 'runner', 'bin', 'Debug', 'net10.0', 'agent-host.dll');
  const runnerArgs = [
    runnerDll,
    '--poll',
    '--server', serverUrl,
    '--runner-id', `restart-${runId}`,
    '--runner-name', `restart-${runId}`,
    '--hostname', 'restart-host',
    '--backend-name', 'remote-test-suite',
    '--git-remote', origin,
    '--git-push-remote', origin,
    '--workdir', runnerWork,
    '--state-dir', stateDir,
    '--base-branch', manifest.fixture.defaultBranch,
    '--cli', '/bin/sh',
    '--cli-args', fakeCli,
    '--ttl', '60',
    '--max-parallelism', '1',
    '--poll-seconds', '1'
  ];
  const startRunner = () => capturedProcess(
    'dotnet',
    runnerArgs,
    repoRoot,
    {
      RUNNER_HEARTBEAT_SECONDS: '2',
      RUNNER_RUN_TIMEOUT_SECONDS: '120',
      REMOTE_REPLAY_CONTROL_DIR: control
    });
  let runner;
  const children = new Set();
  const startTracked = () => {
    runner = startRunner();
    children.add(runner);
    return runner;
  };
  try {
    await hook('claim', 'before');
    startTracked();
    const primarySlot = await waitForSlot(stateDir, task.taskKey, runner);
    const primary = await waitForTask(api, ids.projectId, task.taskId, '3-progress', runner);
    await hook('claim', 'after', {
      taskKey: task.taskKey,
      runAttemptId: primarySlot.runId,
      fence: primarySlot.lease.fencingToken
    });

    const liveCwd = await readlink(`/proc/${primarySlot.processId}/cwd`);
    checks.check(
      'attempt-authority-four-tuple-persisted',
      Number.isInteger(primarySlot.processId)
        && !Number.isNaN(Date.parse(primarySlot.processStartedAtUtc))
        && path.resolve(liveCwd) === path.resolve(primarySlot.worktreePath)
        && Number.isInteger(primarySlot.lease.authorityEpoch)
        && primarySlot.lease.fencingToken > 0
        && primary.state === '3-progress',
      `pid=${primarySlot.processId}, started=${primarySlot.processStartedAtUtc}, `
      + `worktree=${primarySlot.worktreePath}, epoch=${primarySlot.lease.authorityEpoch}, `
      + `fence=${primarySlot.lease.fencingToken}`);

    await hook('run', 'before');
    await stopProcess(runner, 'SIGTERM');
    children.delete(runner);
    if (!pidAlive(primarySlot.processId)) {
      throw new Error('Planned daemon restart killed the detached worker.');
    }
    const replacement = startTracked();
    await waitForOutput(
      replacement,
      `persisted attempt accepted task=${task.taskKey}`,
      30_000);
    await waitForOutput(replacement, 'verification=live process and worktree match', 30_000);
    const adoptedSlot = await waitForSlot(stateDir, task.taskKey, replacement);
    checks.check(
      'proven-live-generation-adopted',
      adoptedSlot.processId === primarySlot.processId
        && adoptedSlot.processStartedAtUtc === primarySlot.processStartedAtUtc
        && adoptedSlot.worktreePath === primarySlot.worktreePath
        && adoptedSlot.lease.authorityEpoch === primarySlot.lease.authorityEpoch
        && adoptedSlot.lease.fencingToken === primarySlot.lease.fencingToken,
      `replacement retained pid/start/worktree/epoch/fence for ${adoptedSlot.attemptId}`);
    await writeFile(path.join(control, 'release-1'), 'complete\n');
    const primaryDone = await waitForTask(
      api,
      ids.projectId,
      task.taskId,
      '4-auto-review',
      replacement,
      45_000);
    await stopProcess(replacement, 'SIGTERM');
    children.delete(replacement);
    const primaryHistory = await api.get(
      `/api/v1/projects/${ids.projectId}/tasks/${task.taskId}/history`);
    const auditAfterPrimary = await api.get('/api/v1/management/audit?after=0');
    checks.check(
      'adopted-generation-completed-once',
      primaryDone.state === '4-auto-review'
        && primaryHistory.runs.length === 1
        && auditAfterPrimary.filter(record =>
          record.action === 'run.completed' && record.targetId === primarySlot.runId).length === 1,
      `task=${primaryDone.state}, runs=${primaryHistory.runs.length}, one completion audit`);
    await hook('run', 'after', {
      pid: primarySlot.processId,
      authorityEpoch: primarySlot.lease.authorityEpoch,
      fence: primarySlot.lease.fencingToken,
      taskState: primaryDone.state
    });

    await hook('gate', 'before');
    const deadTask = await api.post(`/api/v1/projects/${ids.projectId}/tasks`, {
      title: 'Dead persisted generation',
      body: 'The worker is killed while the daemon is down.',
      state: '2-ready',
      taskId: `${ids.taskId}-dead`,
      taskKey: 'RTS-22'
    });
    const deadRunner = startTracked();
    const deadSlot = await waitForSlot(stateDir, deadTask.taskKey, deadRunner);
    await stopProcess(deadRunner, 'SIGTERM');
    children.delete(deadRunner);
    process.kill(deadSlot.processId, 'SIGKILL');
    await waitForPidExit(deadSlot.processId);
    await setServerMode(api, 1, 'dead-generation release replay');
    const deadReplacement = startTracked();
    await waitForOutput(deadReplacement, `releasing dead persisted attempt task=${deadTask.taskKey}`, 30_000);
    await waitForOutput(deadReplacement, 'lease released: released', 30_000);
    const deadReady = await waitForTask(
      api,
      ids.projectId,
      deadTask.taskId,
      '2-ready',
      deadReplacement);
    await stopProcess(deadReplacement, 'SIGTERM');
    children.delete(deadReplacement);
    checks.check(
      'dead-generation-released-to-ready',
      deadReady.state === '2-ready' && !pidAlive(deadSlot.processId),
      `dead pid ${deadSlot.processId} released run ${deadSlot.runId} to ${deadReady.state}`);
    const deadStale = await staleWriterStatuses(context, deadSlot, origin, 'dead');
    // The release assertion deliberately leaves the fixture card Ready. Park it
    // before enabling claims for the next generation so the real runner cannot
    // legitimately reclaim RTS-22 while this scenario is constructing RTS-23.
    await api.put(`/api/v1/projects/${ids.projectId}/tasks/${deadTask.taskId}`, {
      title: null,
      body: null,
      state: '5-human-review',
      expectedVersion: deadReady.version
    });
    await hook('gate', 'after', {
      taskState: deadReady.state,
      staleWriterStatuses: deadStale
    });

    await hook('review', 'before');
    await setServerMode(api, 0, 'mismatched-generation setup');
    const mismatchTask = await api.post(`/api/v1/projects/${ids.projectId}/tasks`, {
      title: 'Mismatched persisted generation',
      body: 'The persisted PID generation is deliberately stale.',
      state: '2-ready',
      taskId: `${ids.taskId}-mismatch`,
      taskKey: 'RTS-23'
    });
    const mismatchRunner = startTracked();
    const mismatchSlot = await waitForSlot(stateDir, mismatchTask.taskKey, mismatchRunner);
    await stopProcess(mismatchRunner, 'SIGTERM');
    children.delete(mismatchRunner);
    const mismatchPath = await slotPath(stateDir, mismatchTask.taskKey);
    await writeFile(
      mismatchPath,
      `${JSON.stringify({
        ...mismatchSlot,
        processStartedAtUtc: '2000-01-01T00:00:00.000Z'
      }, null, 2)}\n`);
    await setServerMode(api, 1, 'mismatched-generation release replay');
    const mismatchReplacement = startTracked();
    await waitForOutput(mismatchReplacement, 'PID was reused (process start time differs)', 30_000);
    await waitForOutput(mismatchReplacement, 'lease released: released', 30_000);
    const mismatchReady = await waitForTask(
      api,
      ids.projectId,
      mismatchTask.taskId,
      '2-ready',
      mismatchReplacement);
    await stopProcess(mismatchReplacement, 'SIGTERM');
    children.delete(mismatchReplacement);
    if (pidAlive(mismatchSlot.processId)) {
      process.kill(mismatchSlot.processId, 'SIGKILL');
      await waitForPidExit(mismatchSlot.processId);
    }
    checks.check(
      'mismatched-generation-released-to-ready',
      mismatchReady.state === '2-ready',
      `mismatched pid generation released run ${mismatchSlot.runId} to ${mismatchReady.state}`);
    const mismatchStale = await staleWriterStatuses(
      context,
      mismatchSlot,
      origin,
      'mismatch');
    await hook('review', 'after', {
      taskState: mismatchReady.state,
      staleWriterStatuses: mismatchStale
    });

    await hook('integration', 'before');
    const allStale = [...deadStale, ...mismatchStale];
    checks.check(
      'all-stale-writers-rejected',
      allStale.length === 8 && allStale.every(status => status === 409),
      `renew/event/handoff/completion statuses=${allStale.join(',')}`);
    await setServerMode(api, 0, 'restart replay complete');
    const terminal = await api.get(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`);
    await hook('integration', 'after', {
      status: 'primary-generation-delivered',
      taskState: terminal.state,
      deadTaskState: deadReady.state,
      mismatchedTaskState: mismatchReady.state
    });

    return finish(
      context,
      checks,
      terminal.state,
      1,
      {
        runAttemptId: primarySlot.runId,
        pid: primarySlot.processId,
        processStartedAtUtc: primarySlot.processStartedAtUtc,
        worktreePath: primarySlot.worktreePath,
        authorityEpoch: primarySlot.lease.authorityEpoch,
        fence: primarySlot.lease.fencingToken,
        deadRunAttemptId: deadSlot.runId,
        mismatchedRunAttemptId: mismatchSlot.runId
      });
  } finally {
    for (const child of children) {
      await stopProcess(child, 'SIGTERM').catch(() => {});
    }
    await setServerMode(api, 0, 'restart replay cleanup').catch(() => {});
  }
}

async function externalCompletionCycle(context) {
  const { manifest, root, seed, runId, api, hook } = context;
  const checks = scenarioAssertions(manifest);
  const { ids, task, variables } = await provision(context);
  const origin = path.join(root, 'fixture-origin.git');

  await hook('claim', 'before');
  const coding = await claimCoding(context, 'external');
  await hook('claim', 'after', {
    taskKey: task.taskKey,
    runAttemptId: coding.claim.run.runId,
    fence: coding.claim.lease.fence
  });

  await hook('run', 'before');
  const deliveredRepo = path.join(root, 'external', 'delivered');
  await mkdir(path.dirname(deliveredRepo), { recursive: true });
  await runCommand(['git', 'clone', origin, deliveredRepo], { cwd: root });
  await configureGit(deliveredRepo, 'External Completion Replay');
  const baseSha = await revParse(deliveredRepo, 'HEAD');
  await runCommand(
    manifest.fixture.changeCommand.map(value => interpolate(value, variables)),
    { cwd: deliveredRepo });
  await runCommand(['git', 'add', '.'], { cwd: deliveredRepo });
  await commit(deliveredRepo, `feat(${task.taskKey}): completed out of band`, seed, 4);
  const resultSha = await revParse(deliveredRepo, 'HEAD');
  const resultRef = `refs/heads/agent-studio/results/${coding.claim.run.runId}/${resultSha}`;
  await runCommand(
    ['git', 'push', 'origin', `HEAD:${resultRef}`],
    { cwd: deliveredRepo });
  const result = resultIdentity(origin, coding.claim.run.runId, baseSha, resultSha, resultRef);
  await hook('run', 'after', { baseSha, resultSha, resultRef });

  await hook('gate', 'before');
  await runCommand(manifest.fixture.acceptanceCommand, { cwd: deliveredRepo });
  const handoff = handoffRequest(coding, result, `${runId}:external-handoff`, 1);
  await api.put(`/api/v1/runs/${coding.claim.run.runId}/result-handoff`, handoff);
  const completion = {
    runnerId: coding.runnerId,
    instanceId: coding.instanceId,
    leaseId: coding.claim.lease.leaseId,
    fence: coding.claim.lease.fence,
    outcome: 'Done',
    summary: 'Out-of-band result reconciled with exact immutable evidence.',
    resultEnvelopeDigest: result.envelopeDigest,
    idempotencyKey: `${runId}:external-completion`,
    sequence: 2
  };
  await api.post(`/api/v1/runs/${coding.claim.run.runId}/completion`, completion);
  await api.post(`/api/v1/runs/${coding.claim.run.runId}/completion`, completion);
  const audit = await api.get('/api/v1/management/audit?after=0');
  checks.check(
    'external-result-reconciled-once',
    audit.filter(record =>
      record.action === 'run.completed'
      && record.targetId === coding.claim.run.runId).length === 1,
    `completion ${completion.idempotencyKey} produced one durable transition`);
  await hook('gate', 'after', { status: 'reconciled-once', resultSha });

  await hook('review', 'before');
  const exactHandoff = await api.get(`/api/v1/runs/${coding.claim.run.runId}/result-handoff`);
  checks.check(
    'exact-result-evidence-retained',
    exactHandoff.envelope.baseSha === baseSha
      && exactHandoff.envelope.resultSha === resultSha
      && exactHandoff.envelope.immutableRemoteRef === resultRef
      && exactHandoff.envelopeDigest === result.envelopeDigest,
    `retained envelope ${exactHandoff.envelopeDigest} binds ${baseSha}..${resultSha}`);
  const delivered = await api.get(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`);
  await api.put(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`, {
    title: null,
    body: null,
    state: '2-ready',
    expectedVersion: delivered.version
  });
  await api.post(`/api/v1/runs/${coding.claim.run.runId}/completion`, completion);
  const afterReplay = await api.get(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`);
  checks.check(
    'completion-replay-does-not-move-requeued-task',
    afterReplay.state === '2-ready',
    `idempotent old completion left operator-requeued task in ${afterReplay.state}`);
  await hook('review', 'after', {
    status: 'old-completion-replay-contained',
    taskState: afterReplay.state
  });

  await hook('integration', 'before');
  const retry = await claimCoding(context, 'external-retry');
  const strandedRepo = path.join(root, 'external', 'stranded-worktree');
  await runCommand(['git', 'clone', origin, strandedRepo], { cwd: root });
  await configureGit(strandedRepo, 'Stranded Salvage Replay');
  await writeFile(
    path.join(strandedRepo, 'recoverable-local-work.txt'),
    `unpublished salvage for ${task.taskKey}\n`);
  await runCommand(['git', 'add', 'recoverable-local-work.txt'], { cwd: strandedRepo });
  await commit(strandedRepo, `wip(${task.taskKey}): recoverable local salvage`, seed, 5);
  const strandedSha = await revParse(strandedRepo, 'HEAD');
  const rejected = await api.attempt(
    'POST',
    `/api/v1/runs/${retry.claim.run.runId}/completion`,
    {
      runnerId: retry.runnerId,
      instanceId: retry.instanceId,
      leaseId: retry.claim.lease.leaseId,
      fence: retry.claim.lease.fence,
      outcome: 'Done',
      summary: 'This must not be delivered because salvage publication failed.',
      idempotencyKey: `${runId}:failed-salvage-completion`,
      sequence: 1
    });
  checks.check(
    'failed-salvage-not-delivered',
    rejected.status === 409 && rejected.text.includes('result-handoff-required'),
    `failed salvage completion returned ${rejected.status}: ${rejected.text}`);
  const localStillPresent = await revParse(strandedRepo, 'HEAD');
  const remoteProbe = await commandStatus(
    ['git', '--git-dir', origin, 'rev-parse',
      `refs/heads/agent-studio/results/${retry.claim.run.runId}/${strandedSha}`],
    root);
  checks.check(
    'stranded-worktree-preserved',
    localStillPresent === strandedSha && remoteProbe.code !== 0,
    `local ${strandedSha} remains at ${strandedRepo}; no immutable result ref exists`);
  await api.post(`/api/v1/runs/${retry.claim.run.runId}/lease/release`, {
    runnerId: retry.runnerId,
    instanceId: retry.instanceId,
    leaseId: retry.claim.lease.leaseId,
    fence: retry.claim.lease.fence,
    outcome: 'salvage-failed-worktree-retained'
  });
  const finalTask = await api.get(`/api/v1/projects/${ids.projectId}/tasks/${task.taskId}`);
  const finalHistory = await api.get(
    `/api/v1/projects/${ids.projectId}/tasks/${task.taskId}/history`);
  const finalAudit = await api.get('/api/v1/management/audit?after=0');
  checks.check(
    'external-completion-loop-bounded',
    finalTask.state === '2-ready'
      && finalHistory.runs.length === 2
      && finalAudit.filter(record => record.action === 'run.completed').length === 1
      && finalAudit.filter(record => record.action === 'lease.released').length === 1,
    `runs=${finalHistory.runs.length}, completions=1, failed-salvage releases=1, terminal=${finalTask.state}`);
  await hook('integration', 'after', {
    status: 'failed-salvage-retained-not-delivered',
    taskState: finalTask.state,
    strandedSha,
    strandedWorktree: strandedRepo
  });

  return finish(
    context,
    checks,
    finalTask.state,
    1,
    {
      sourceRunAttemptId: coding.claim.run.runId,
      retryRunAttemptId: retry.claim.run.runId,
      baseSha,
      resultSha,
      resultRef,
      envelopeDigest: result.envelopeDigest,
      strandedSha,
      strandedWorktree: strandedRepo
    });
}

function finish(context, checks, actualTerminal, recoveryUsed, evidence) {
  const contract = checks.finish(actualTerminal, recoveryUsed);
  return {
    scenario: context.manifest.name,
    seed: context.seed,
    runId: context.runId,
    accepted: true,
    ...contract,
    phaseSequence: [...context.manifest.phases],
    evidence,
    resourcesRoot: context.root
  };
}

async function createReviewSubject(context, task, runId, result, idempotencyKey) {
  const { manifest, api } = context;
  return await api.post('/api/v1/reviews/subjects', {
    taskId: task.taskId,
    sourceRunId: runId,
    repositoryId: result.envelope.repositoryId,
    repositoryUrl: result.envelope.repositoryUrl,
    expectedResultSha: result.envelope.resultSha,
    resultRef: result.envelope.immutableRemoteRef,
    sourceBundleArtifactId: null,
    sourceBundleSha256: null,
    codingHostId: 'coding-host',
    reviewPolicyHash: sha256('historical-replay-review-policy-v1'),
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
    idempotencyKey
  });
}

async function executeReviewAttempt(
  context,
  reviewer,
  repo,
  result,
  actualHead,
  idempotencyKey,
  runAcceptance) {
  const { api, manifest } = context;
  const claim = await api.post(`/api/v1/runners/${reviewer.runnerId}/review-claims`, {
    executorId: reviewer.runnerId,
    instanceId: reviewer.instanceId,
    requestedTtlSeconds: 120,
    availableSlots: 1
  });
  if (claim.status !== 'claimed') {
    throw new Error(`Review replay was not claimed: ${JSON.stringify(claim)}`);
  }
  const tree = (await runCommand(
    ['git', 'show', '-s', '--format=%T', actualHead],
    { cwd: repo })).stdout.trim();
  let stdout = '';
  let stderr = '';
  if (runAcceptance) {
    await runCommand(['git', 'checkout', '--detach', actualHead], { cwd: repo });
    const acceptance = await runCommand(manifest.fixture.acceptanceCommand, { cwd: repo });
    stdout = acceptance.stdout;
    stderr = acceptance.stderr;
  }
  const startedAt = new Date().toISOString();
  const finishedAt = new Date().toISOString();
  const workspacePath = `/review/${claim.lease.resourceNamespace}`;
  const stdoutSha256 = sha256(stdout);
  const stderrSha256 = sha256(stderr);
  const report = await api.post(
    `/api/v1/reviews/attempts/${claim.attempt.attemptId}/report`,
    {
      executorId: reviewer.runnerId,
      instanceId: reviewer.instanceId,
      leaseId: claim.lease.leaseId,
      fence: claim.lease.fence,
      authorityEpoch: claim.lease.authorityEpoch,
      idempotencyKey,
      outcome: 'Pass',
      failureClassification: null,
      summary: runAcceptance
        ? 'Exact immutable Result-SHA semantic acceptance passed.'
        : 'This deliberately wrong review scope must be rejected.',
      workspace: {
        repositoryId: result.envelope.repositoryId,
        expectedResultSha: result.envelope.resultSha,
        actualHead,
        treeHash: tree,
        dirtyBefore: false,
        dirtyAfter: false,
        workspaceIdentity: sha256(workspacePath),
        resourceNamespace: claim.lease.resourceNamespace
      },
      environment: {
        hostId: reviewer.hostId,
        executorId: reviewer.runnerId,
        instanceId: reviewer.instanceId,
        osDescription: process.platform,
        architecture: process.arch,
        runtimeVersion: process.version,
        toolchain: {
          runtime: process.version,
          git: `git;sha256=${'d'.repeat(64)}`,
          'command:semantic-acceptance':
            `${manifest.fixture.acceptanceCommand[0]};sha256=${'e'.repeat(64)}`
        },
        isolation: {
          workspace: workspacePath,
          cache: `${workspacePath}/cache`,
          temp: `${workspacePath}/tmp`,
          ports: `${claim.lease.portBase}-${claim.lease.portBase + 7}`,
          containers: claim.lease.resourceNamespace,
          databases: claim.lease.resourceNamespace,
          credentials: 'review-read-only'
        }
      },
      commands: [{
        stepId: 'semantic-acceptance',
        aspect: 'semantic',
        fileName: manifest.fixture.acceptanceCommand[0],
        arguments: manifest.fixture.acceptanceCommand.slice(1),
        expectedResultSha: result.envelope.resultSha,
        headBefore: actualHead,
        treeBefore: tree,
        startedAt,
        finishedAt,
        exitCode: 0,
        signal: null,
        stdoutSha256,
        stderrSha256
      }],
      artifacts: [
        {
          name: 'semantic-acceptance.stdout.log',
          mediaType: 'text/plain',
          sha256: stdoutSha256,
          sizeBytes: Buffer.byteLength(stdout)
        },
        {
          name: 'semantic-acceptance.stderr.log',
          mediaType: 'text/plain',
          sha256: stderrSha256,
          sizeBytes: Buffer.byteLength(stderr)
        }
      ],
      verdicts: [{
        aspect: 'semantic',
        status: 'pass',
        classification: 'Accepted',
        summary: 'Deterministic semantic acceptance passed.'
      }]
    });
  return { claim, report };
}

function resultIdentity(origin, runId, baseSha, resultSha, resultRef) {
  const repositoryUrl = pathToFileURL(origin).href;
  const repositoryId = `repo_${sha256(repositoryUrl.trim().replace(/\/$/, '').toLowerCase())}`;
  const envelope = {
    repositoryId,
    sourceRunAttemptId: runId,
    baseSha,
    resultSha,
    immutableRemoteRef: resultRef,
    sourceBundleDigest: null,
    artifactManifestDigest: sha256('[]'),
    submodules: [],
    lfsObjects: [],
    repositoryUrl
  };
  const envelopeDigest = sha256(JSON.stringify({ ...envelope, repositoryUrl: null }));
  return { envelope, envelopeDigest };
}

function handoffRequest(coding, result, idempotencyKey, sequence) {
  return {
    runnerId: coding.runnerId,
    instanceId: coding.instanceId,
    leaseId: coding.claim.lease.leaseId,
    fence: coding.claim.lease.fence,
    sequence,
    idempotencyKey,
    envelopeDigest: result.envelopeDigest,
    envelope: result.envelope
  };
}

async function staleWriterStatuses(context, slot, origin, label) {
  const { api, runId } = context;
  const authority = {
    runnerId: slot.lease.runnerId,
    instanceId: slot.leaseInstanceId,
    leaseId: slot.lease.leaseId,
    fence: slot.lease.fencingToken
  };
  const renew = await api.attempt(
    'POST',
    `/api/v1/runs/${slot.runId}/lease/renew`,
    {
      ...authority,
      requestedTtlSeconds: 120
    });
  const event = await api.attempt(
    'POST',
    `/api/v1/runs/${slot.runId}/events`,
    {
      eventId: `evt-${label}-${runId}`,
      kind: 'runner.stale-writer-probe',
      payloadJson: '{}',
      idempotencyKey: `${runId}:${label}:stale-event`,
      fence: authority.fence,
      runnerId: authority.runnerId,
      instanceId: authority.instanceId,
      leaseId: authority.leaseId,
      sequence: 90
    });
  const staleResultSha = '2'.repeat(40);
  const staleResult = resultIdentity(
    origin,
    slot.runId,
    '1'.repeat(40),
    staleResultSha,
    `refs/heads/agent-studio/results/${slot.runId}/${staleResultSha}`);
  const handoff = await api.attempt(
    'PUT',
    `/api/v1/runs/${slot.runId}/result-handoff`,
    {
      ...authority,
      sequence: 91,
      idempotencyKey: `${runId}:${label}:stale-handoff`,
      envelopeDigest: staleResult.envelopeDigest,
      envelope: staleResult.envelope
    });
  const completion = await api.attempt(
    'POST',
    `/api/v1/runs/${slot.runId}/completion`,
    {
      ...authority,
      outcome: 'blocked',
      summary: 'stale writer probe',
      idempotencyKey: `${runId}:${label}:stale-completion`,
      sequence: 92
    });
  return [renew.status, event.status, handoff.status, completion.status];
}

async function configureGit(repo, name) {
  await runCommand(['git', 'config', 'user.name', name], { cwd: repo });
  await runCommand(['git', 'config', 'user.email', 'remote-test-suite@invalid.local'], { cwd: repo });
}

async function commit(repo, message, seed, offset) {
  const date = deterministicDate(seed, offset);
  await runCommand(['git', 'commit', '-m', message], {
    cwd: repo,
    env: { GIT_AUTHOR_DATE: date, GIT_COMMITTER_DATE: date }
  });
}

function deterministicDate(seed, offset = 0) {
  const seconds = Number.parseInt(sha256(seed).slice(0, 8), 16)
    % (20 * 365 * 24 * 60 * 60);
  return new Date(Date.UTC(2020, 0, 1) + (seconds + offset) * 1000).toISOString();
}

async function revParse(repo, revision) {
  return (await runCommand(['git', 'rev-parse', revision], { cwd: repo })).stdout.trim();
}

async function changedFiles(repo, baseSha, resultSha) {
  const output = (await runCommand(
    ['git', 'diff', '--name-only', `${baseSha}..${resultSha}`],
    { cwd: repo })).stdout.trim();
  return output.length === 0 ? [] : output.split(/\r?\n/).sort();
}

async function containsCommit(repo, baseSha, resultSha, commitSha) {
  const commits = (await runCommand(
    ['git', 'rev-list', `${baseSha}..${resultSha}`],
    { cwd: repo })).stdout.trim().split(/\r?\n/).filter(Boolean);
  return commits.includes(commitSha);
}

async function commandStatus(command, cwd) {
  try {
    const result = await runCommand(command, { cwd });
    return { code: result.code, stdout: result.stdout, stderr: result.stderr };
  } catch (error) {
    return { code: 1, stdout: '', stderr: error.message };
  }
}

async function writeRestartWorker(file) {
  await writeFile(file, `#!/bin/sh
set -eu
counter="$REMOTE_REPLAY_CONTROL_DIR/invocations"
attempt=0
if [ -f "$counter" ]; then attempt=$(cat "$counter"); fi
attempt=$((attempt + 1))
printf '%s' "$attempt" > "$counter"
printf 'ready\\n' > "$REMOTE_REPLAY_CONTROL_DIR/ready-$attempt"
while [ ! -f "$REMOTE_REPLAY_CONTROL_DIR/release-$attempt" ]; do
  sleep 0.05
done
mkdir -p "$JOB_RESULTS_DIR"
printf 'restart replay attempt %s\\n' "$attempt" > "$JOB_RESULTS_DIR/restart-$attempt.txt"
printf '{"type":"agent_message","text":"restart replay attempt %s complete"}\\n' "$attempt"
printf '[[TASK_DONE]]\\n'
`);
  await chmod(file, 0o700);
}

function capturedProcess(file, args, cwd, env = {}) {
  const child = spawn(file, args, {
    cwd,
    env: { ...process.env, ...env },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  child.output = '';
  child.stdout.on('data', chunk => { child.output += chunk; });
  child.stderr.on('data', chunk => { child.output += chunk; });
  return child;
}

async function waitForOutput(child, text, timeoutMs) {
  await waitUntil(
    () => child.output.includes(text),
    timeoutMs,
    child,
    `output containing '${text}'`);
}

async function waitForTask(api, projectId, taskId, state, child, timeoutMs = 30_000) {
  let current;
  await waitUntil(async () => {
    current = await api.get(`/api/v1/projects/${projectId}/tasks/${taskId}`);
    return current.state === state;
  }, timeoutMs, child, `task ${taskId} state ${state}`);
  return current;
}

async function waitForSlot(stateDir, taskKey, child, timeoutMs = 30_000) {
  let slot;
  await waitUntil(async () => {
    const file = await slotPath(stateDir, taskKey).catch(() => null);
    if (!file) return false;
    slot = JSON.parse(await readFile(file, 'utf8'));
    return slot.processId > 0 && slot.processStartedAtUtc && slot.phase === 'running';
  }, timeoutMs, child, `persisted running slot for ${taskKey}`);
  return slot;
}

async function slotPath(stateDir, taskKey) {
  const files = await readdir(stateDir);
  for (const file of files.filter(value => value.endsWith('.slot.json'))) {
    const full = path.join(stateDir, file);
    const slot = JSON.parse(await readFile(full, 'utf8'));
    if (slot.taskKey === taskKey) return full;
  }
  throw new Error(`No persisted slot exists for ${taskKey}.`);
}

async function waitUntil(predicate, timeoutMs, child, label) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (child?.exitCode !== null) {
      throw new Error(`Process exited while waiting for ${label}:\n${child.output}`);
    }
    if (await predicate()) return;
    await delay(100);
  }
  throw new Error(`Timed out waiting for ${label}:\n${child?.output ?? ''}`);
}

async function stopProcess(child, signal) {
  if (child.exitCode !== null) return;
  child.kill(signal);
  const deadline = Date.now() + 15_000;
  while (child.exitCode === null && Date.now() < deadline) await delay(100);
  if (child.exitCode === null) {
    child.kill('SIGKILL');
    while (child.exitCode === null) await delay(50);
  }
}

function pidAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

async function waitForPidExit(pid) {
  const deadline = Date.now() + 10_000;
  while (pidAlive(pid) && Date.now() < deadline) await delay(50);
  if (pidAlive(pid)) throw new Error(`PID ${pid} did not exit.`);
}

async function setServerMode(api, mode, reason) {
  await api.put('/api/v1/management/mode', { mode, reason });
}

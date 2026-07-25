#!/usr/bin/env node

import { mkdir, opendir, readFile, rename, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const SCRIPT_VERSION = 1;
const DEFAULT_OUTPUT_DIR = process.env.JOB_RESULTS_DIR ?? 'results';
const TERMINAL_ABORT_LANES = new Set([
  '3a-failed-pickup',
  '5e-escalated',
  'aborted',
  'cancelled',
  'canceled',
  'failed',
]);
const WALK_SKIP_DIRECTORIES = new Set(['.git', 'node_modules', 'bin', 'obj']);

function parseArguments(argv) {
  const options = {
    taskRoot: null,
    snapshot: null,
    outputDir: DEFAULT_OUTPUT_DIR,
    sourceLabel: null,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    const value = argv[index + 1];
    if (argument === '--task-root' && value) options.taskRoot = value, index += 1;
    else if (argument === '--snapshot' && value) options.snapshot = value, index += 1;
    else if (argument === '--output-dir' && value) options.outputDir = value, index += 1;
    else if (argument === '--source-label' && value) options.sourceLabel = value, index += 1;
    else if (argument === '--help') options.help = true;
    else throw new Error(`Unknown or incomplete argument: ${argument}`);
  }
  if (options.taskRoot && options.snapshot) {
    throw new Error('Use either --task-root or --snapshot, not both.');
  }
  if (!options.taskRoot && !options.snapshot) options.taskRoot = process.env.TASK_REPOSITORY ?? null;
  return options;
}

function usage() {
  return [
    'Usage:',
    '  node scripts/model-benchmark-aggregate.mjs --task-root PATH [--output-dir results]',
    '  node scripts/model-benchmark-aggregate.mjs --snapshot tasks.json [--source-label LABEL]',
    '',
    'The task root may be a central TaskRepository or a single legacy project store.',
    'TASK_REPOSITORY is used when --task-root is omitted. --snapshot accepts a read-only',
    'API task-array snapshot and is intended for hosts that cannot mount the task store.',
  ].join('\n');
}

function stringValue(value, fallback = 'unknown') {
  return typeof value === 'string' && value.trim() ? value.trim() : fallback;
}

function normalizeTaskType(value) {
  const normalized = stringValue(value, 'chore').toLowerCase();
  return normalized === 'user-story' ? 'feature' : normalized;
}

function numberValue(value) {
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value !== 'string') return null;
  const compact = value.trim().toLowerCase().replaceAll(',', '').replaceAll('_', '');
  const match = compact.match(/(-?\d+(?:\.\d+)?)\s*([kmb])?/);
  if (!match) return null;
  const multiplier = { k: 1_000, m: 1_000_000, b: 1_000_000_000 }[match[2]] ?? 1;
  return Number(match[1]) * multiplier;
}

function firstNumber(...values) {
  for (const value of values) {
    const parsed = numberValue(value);
    if (parsed !== null && parsed >= 0) return parsed;
  }
  return null;
}

function tokenTotal(task) {
  const summary = task.tokenSummary ?? task.TokenSummary;
  if (summary && typeof summary === 'object') {
    const explicit = firstNumber(summary.totalTokens, summary.TotalTokens, summary.total, summary.tokens);
    if (explicit !== null) return explicit;
    const components = [
      summary.inputTokens ?? summary.InputTokens,
      summary.outputTokens ?? summary.OutputTokens,
      summary.cacheReadTokens ?? summary.CacheReadTokens,
      summary.cacheCreationTokens ?? summary.CacheCreationTokens,
    ].map(numberValue);
    if (components.some(value => value !== null)) {
      return components.reduce((sum, value) => sum + (value ?? 0), 0);
    }
  }
  const usage = task.lastUsage ?? task.LastUsage;
  if (usage && typeof usage === 'object') {
    return firstNumber(usage.totalTokens, usage.TotalTokens, usage.tokens, usage.Tokens);
  }
  return firstNumber(task.totalTokens, task.tokens);
}

function normalizeGrade(task) {
  const candidates = [
    task.orchestratorGrade,
    task.OrchestratorGrade,
    task.reviewGrade,
    task.ReviewGrade,
    task.grade,
    task.Grade,
    task.codeReviewGrade,
    task.orchestratorVerdict?.grade,
    task.OrchestratorVerdict?.Grade,
    task.orchestrator?.grade,
  ];
  for (const candidate of candidates) {
    const match = String(candidate ?? '').toUpperCase().match(/(?:GRADE[\s:_-]*)?([A-D])\b/);
    if (match) return match[1];
  }
  for (const tag of task.tags ?? task.Tags ?? []) {
    const match = String(tag).toUpperCase().match(/CODE[-_ ]?REVIEW(?::|-)?GRADE(?::|-)?([A-D])\b/);
    if (match) return match[1];
  }
  return 'unknown';
}

function normalizeTransitions(task) {
  const transitions = task.provenance?.transitions
    ?? task.provenance?.Transitions
    ?? task.Provenance?.transitions
    ?? task.Provenance?.Transitions
    ?? [];
  if (!Array.isArray(transitions)) return [];
  return transitions
    .map(transition => {
      const lane = stringValue(
        transition.lane ?? transition.Lane ?? transition.to ?? transition.To ?? transition.state,
        '',
      ).toLowerCase();
      const rawAt = transition.atUtc ?? transition.AtUtc ?? transition.at ?? transition.At ?? transition.timestamp;
      const atMs = Date.parse(rawAt);
      return { lane, atMs: Number.isFinite(atMs) ? atMs : null };
    })
    .filter(transition => transition.lane || transition.atMs !== null)
    .sort((left, right) => (left.atMs ?? Number.MAX_SAFE_INTEGER) - (right.atMs ?? Number.MAX_SAFE_INTEGER));
}

function reissueRounds(task, transitions) {
  const explicit = firstNumber(
    task.reissueRounds,
    task.ReissueRounds,
    task.provenance?.reissueRounds,
    task.provenance?.ReissueRounds,
  );
  const progressEntries = transitions.filter(transition => transition.lane === '3-progress').length;
  return Math.max(explicit ?? 0, Math.max(0, progressEntries - 1));
}

function durationSeconds(task, transitions) {
  const dated = transitions.filter(transition => transition.atMs !== null);
  if (dated.length < 2) return null;
  const firstProgress = dated.find(transition => transition.lane === '3-progress');
  const start = firstProgress?.atMs ?? dated[0].atMs;
  const end = dated.at(-1).atMs;
  if (start === null || end === null || end < start) return null;
  return Math.round((end - start) / 1_000);
}

function inferProject(sourcePath, task) {
  const explicit = task.projectName ?? task.ProjectName ?? task.project ?? task.Project;
  if (explicit) return stringValue(explicit);
  const parts = sourcePath.split(/[\\/]/);
  const projectsIndex = parts.lastIndexOf('projects');
  if (projectsIndex >= 0 && parts[projectsIndex + 1]) return parts[projectsIndex + 1];
  return 'unknown';
}

function normalizeTask(task, sourcePath) {
  const transitions = normalizeTransitions(task);
  const state = stringValue(task.state ?? task.State ?? task.lane ?? task.Lane).toLowerCase();
  const rounds = reissueRounds(task, transitions);
  const grade = normalizeGrade(task);
  const verdictValue = task.orchestratorVerdict?.verdict
    ?? task.OrchestratorVerdict?.Verdict
    ?? task.orchestratorVerdict
    ?? task.OrchestratorVerdict;
  const verdict = stringValue(verdictValue, 'unknown').toLowerCase();
  const tokens = tokenTotal(task);
  const hasRun = transitions.some(transition => transition.lane === '3-progress')
    || /^(3|4|5|6|7)(?:-|[a-z])/.test(state)
    || Boolean(task.execution ?? task.Execution)
    || tokens !== null
    || verdict !== 'unknown'
    || grade !== 'unknown';
  return {
    id: stringValue(task.key ?? task.Key ?? task.taskKey ?? task.TaskKey ?? task.id ?? task.Id, path.basename(path.dirname(sourcePath))),
    project: inferProject(sourcePath, task),
    model: stringValue(task.model ?? task.Model),
    thinkingLevel: stringValue(task.thinkingLevel ?? task.ThinkingLevel).toLowerCase(),
    cliType: stringValue(task.cliType ?? task.CliType ?? task.agent ?? task.Agent).toLowerCase(),
    taskType: normalizeTaskType(task.taskType ?? task.TaskType),
    grade,
    verdict,
    reissueRounds: rounds,
    durationSeconds: durationSeconds(task, transitions),
    tokenTotal: tokens,
    state,
    aborted: TERMINAL_ABORT_LANES.has(state),
    hasRun,
  };
}

async function isDirectory(candidate) {
  try {
    return (await stat(candidate)).isDirectory();
  } catch {
    return false;
  }
}

async function discoverStorageRoots(taskRoot, warnings) {
  const root = path.resolve(taskRoot);
  if (!await isDirectory(root)) throw new Error(`Task root does not exist or is not a directory: ${root}`);
  const roots = new Set([root]);
  const registryPath = path.join(root, '.metadata', 'projects.json');
  try {
    const registry = JSON.parse(await readFile(registryPath, 'utf8'));
    const projects = registry.projects ?? registry.Projects ?? [];
    for (const project of projects) {
      const storage = project.storageLocation ?? project.StorageLocation;
      if (!storage) continue;
      const resolved = path.isAbsolute(storage) ? storage : path.resolve(root, storage);
      if (await isDirectory(resolved)) roots.add(path.resolve(resolved));
      else warnings.push(`Registry storage is unavailable on this host: ${storage}`);
    }
  } catch (error) {
    if (error?.code !== 'ENOENT') warnings.push(`Could not read project registry: ${error.message}`);
  }
  return [...roots].sort();
}

async function walkTaskFiles(root, output, visitedDirectories) {
  const resolved = path.resolve(root);
  if (visitedDirectories.has(resolved)) return;
  visitedDirectories.add(resolved);
  const directory = await opendir(resolved);
  for await (const entry of directory) {
    if (entry.isDirectory()) {
      if (!WALK_SKIP_DIRECTORIES.has(entry.name)) {
        await walkTaskFiles(path.join(resolved, entry.name), output, visitedDirectories);
      }
    } else if (entry.isFile() && entry.name.toLowerCase() === 'task.json') {
      output.add(path.join(resolved, entry.name));
    }
  }
}

async function loadTaskStore(taskRoot) {
  const warnings = [];
  const roots = await discoverStorageRoots(taskRoot, warnings);
  const files = new Set();
  const visitedDirectories = new Set();
  for (const root of roots) await walkTaskFiles(root, files, visitedDirectories);
  const tasks = [];
  let parseErrors = 0;
  for (const file of [...files].sort()) {
    try {
      const task = JSON.parse(await readFile(file, 'utf8'));
      tasks.push(normalizeTask(task, file));
    } catch (error) {
      parseErrors += 1;
      warnings.push(`Skipped unreadable task file ${path.relative(path.resolve(taskRoot), file)}: ${error.message}`);
    }
  }
  return { tasks, warnings, parseErrors, discovered: files.size, roots };
}

async function loadSnapshot(snapshotPath) {
  const payload = JSON.parse(await readFile(path.resolve(snapshotPath), 'utf8'));
  const rawTasks = Array.isArray(payload) ? payload : payload.tasks ?? payload.items;
  if (!Array.isArray(rawTasks)) throw new Error('Snapshot must be an array or contain a tasks/items array.');
  return {
    tasks: rawTasks.map((task, index) => normalizeTask(task, `snapshot/tasks/${index}/task.json`)),
    warnings: [],
    parseErrors: 0,
    discovered: rawTasks.length,
    roots: [],
  };
}

function median(values) {
  const sorted = values.filter(value => value !== null && Number.isFinite(value)).sort((a, b) => a - b);
  if (!sorted.length) return null;
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
}

function distribution(values, knownKeys = []) {
  const result = Object.fromEntries(knownKeys.map(key => [key, 0]));
  for (const value of values) result[value] = (result[value] ?? 0) + 1;
  return Object.fromEntries(Object.entries(result).sort(([left], [right]) => left.localeCompare(right)));
}

function roundRate(numerator, denominator) {
  return denominator ? Number((numerator / denominator).toFixed(4)) : 0;
}

function aggregateTasks(tasks, metadata = {}) {
  const withDimensions = tasks.filter(task => task.model !== 'unknown' && task.thinkingLevel !== 'unknown');
  const included = withDimensions.filter(task => task.hasRun);
  const grouped = new Map();
  for (const task of included) {
    const key = `${task.model}\u0000${task.thinkingLevel}\u0000${task.taskType}`;
    const values = grouped.get(key) ?? [];
    values.push(task);
    grouped.set(key, values);
  }

  const groups = [...grouped.values()].map(values => {
    const sample = values[0];
    const reissued = values.filter(task => task.reissueRounds > 0).length;
    const aborted = values.filter(task => task.aborted).length;
    const durations = values.map(task => task.durationSeconds).filter(value => value !== null);
    const tokens = values.map(task => task.tokenTotal).filter(value => value !== null);
    return {
      model: sample.model,
      thinkingLevel: sample.thinkingLevel,
      taskType: sample.taskType,
      runCount: values.length,
      cliTypeDistribution: distribution(values.map(task => task.cliType)),
      gradeDistribution: distribution(values.map(task => task.grade), ['A', 'B', 'C', 'D', 'unknown']),
      verdictDistribution: distribution(values.map(task => task.verdict)),
      reissueCount: reissued,
      reissueRate: roundRate(reissued, values.length),
      reissueRoundsTotal: values.reduce((sum, task) => sum + task.reissueRounds, 0),
      medianReissueRounds: median(values.map(task => task.reissueRounds)),
      medianDurationSeconds: median(durations),
      durationSampleCount: durations.length,
      medianTokenUsage: median(tokens),
      tokenSampleCount: tokens.length,
      abortCount: aborted,
      abortRate: roundRate(aborted, values.length),
      laneEndStateDistribution: distribution(values.map(task => task.state)),
    };
  }).sort((left, right) =>
    left.model.localeCompare(right.model)
    || left.thinkingLevel.localeCompare(right.thinkingLevel)
    || left.taskType.localeCompare(right.taskType));

  return {
    schemaVersion: 1,
    generator: { name: 'model-benchmark-aggregate', version: SCRIPT_VERSION },
    source: metadata.source,
    methodology: {
      observationUnit: 'one task record with run evidence',
      grouping: ['model', 'thinkingLevel', 'taskType'],
      reissueDefinition: 'additional transitions into 3-progress after the first, with explicit reissueRounds as fallback',
      durationDefinition: 'first 3-progress transition through latest recorded transition',
      tokenDefinition: 'tokenSummary total or components, then lastUsage tokens; missing samples excluded from median',
      abortDefinition: 'final lane is failed, cancelled, aborted, 3a-failed-pickup, or 5e-escalated',
    },
    summary: {
      taskRecordsDiscovered: metadata.discovered ?? tasks.length,
      taskRecordsIncluded: included.length,
      taskRecordsExcludedMissingModelOrLevel: tasks.length - withDimensions.length,
      taskRecordsExcludedWithoutRunEvidence: withDimensions.length - included.length,
      projectCount: new Set(included.map(task => task.project)).size,
      groupCount: groups.length,
    },
    dataQuality: {
      parseErrors: metadata.parseErrors ?? 0,
      warningCount: metadata.warnings?.length ?? 0,
      warnings: metadata.warnings ?? [],
      gradeCoverageRate: roundRate(included.filter(task => task.grade !== 'unknown').length, included.length),
      durationCoverageRate: roundRate(included.filter(task => task.durationSeconds !== null).length, included.length),
      tokenCoverageRate: roundRate(included.filter(task => task.tokenTotal !== null).length, included.length),
    },
    groups,
  };
}

function formatRate(value) {
  return `${(value * 100).toFixed(1)}%`;
}

function formatDuration(seconds) {
  if (seconds === null) return 'n/a';
  if (seconds < 120) return `${Math.round(seconds)}s`;
  if (seconds < 7_200) return `${(seconds / 60).toFixed(1)}m`;
  if (seconds < 172_800) return `${(seconds / 3_600).toFixed(1)}h`;
  return `${(seconds / 86_400).toFixed(1)}d`;
}

function formatTokens(tokens) {
  if (tokens === null) return 'n/a';
  return Math.round(tokens).toLocaleString('en-US');
}

function compactDistribution(value, order = null) {
  const entries = order
    ? order.map(key => [key, value[key] ?? 0])
    : Object.entries(value);
  return entries.map(([key, count]) => `${key}:${count}`).join(' ');
}

function notableFindings(report) {
  const eligible = report.groups.filter(group => group.runCount >= 3);
  const findings = [];
  if (eligible.length) {
    const highestReissue = [...eligible].sort((a, b) => b.reissueRate - a.reissueRate || b.runCount - a.runCount)[0];
    findings.push(`Highest reissue rate among groups with at least three runs: ${highestReissue.model} / ${highestReissue.thinkingLevel} / ${highestReissue.taskType} at ${formatRate(highestReissue.reissueRate)} (${highestReissue.reissueCount}/${highestReissue.runCount}).`);
    const highestAbort = [...eligible].sort((a, b) => b.abortRate - a.abortRate || b.runCount - a.runCount)[0];
    findings.push(`Highest abort rate among groups with at least three runs: ${highestAbort.model} / ${highestAbort.thinkingLevel} / ${highestAbort.taskType} at ${formatRate(highestAbort.abortRate)} (${highestAbort.abortCount}/${highestAbort.runCount}).`);
  } else {
    findings.push('No group has at least three runs, so comparative outlier claims are withheld.');
  }
  findings.push(`Coverage: grades ${formatRate(report.dataQuality.gradeCoverageRate)}, durations ${formatRate(report.dataQuality.durationCoverageRate)}, tokens ${formatRate(report.dataQuality.tokenCoverageRate)}.`);
  if (report.dataQuality.gradeCoverageRate < 0.5) findings.push('Grade coverage is below 50%; compare grade distributions cautiously.');
  if (report.dataQuality.tokenCoverageRate < 0.5) findings.push('Token coverage is below 50%; token medians are based only on available samples.');
  return findings;
}

function renderMarkdown(report) {
  const lines = [
    '# Historical model benchmark',
    '',
    `Source: ${report.source.label} (${report.source.mode}).`,
    '',
    `This report aggregates ${report.summary.taskRecordsIncluded} task records across ${report.summary.projectCount} projects into ${report.summary.groupCount} model/level/task-type groups. It is observational history, not a controlled fresh-run benchmark. AGT-2200 covers fresh comparison runs.`,
    '',
    '## Aggregate',
    '',
    '| Model | Thinking | Task type | Runs | Grades A/B/C/D/? | Reissue | Median duration | Median tokens | Aborted | End lanes |',
    '|---|---|---:|---:|---|---:|---:|---:|---:|---|',
  ];
  for (const group of report.groups) {
    lines.push(`| ${group.model} | ${group.thinkingLevel} | ${group.taskType} | ${group.runCount} | ${compactDistribution(group.gradeDistribution, ['A', 'B', 'C', 'D', 'unknown'])} | ${formatRate(group.reissueRate)} (${group.reissueCount}) | ${formatDuration(group.medianDurationSeconds)} (${group.durationSampleCount}) | ${formatTokens(group.medianTokenUsage)} (${group.tokenSampleCount}) | ${formatRate(group.abortRate)} (${group.abortCount}) | ${compactDistribution(group.laneEndStateDistribution)} |`);
  }
  lines.push(
    '',
    '## Notable findings',
    '',
    ...notableFindings(report).map(finding => `- ${finding}`),
    '',
    '## Interpretation and data quality',
    '',
    '- One run is one task record. Reissue rounds are additional transitions into `3-progress` after the first entry, or the explicit `reissueRounds` value when present.',
    '- Backlog, preparation, and ready cards without execution, transition, token, verdict, or grade evidence are configurations, not runs, and are excluded.',
    '- Duration runs from the first `3-progress` transition to the latest recorded transition. Groups show the number of usable duration samples in parentheses.',
    '- Token usage prefers `tokenSummary.totalTokens`, then token components, then `lastUsage.tokens`. Medians exclude missing values and show their sample count in parentheses.',
    '- Model and thinking level are the values on the final task record. Historical reissue rounds cannot be split by model when a card changed model between attempts.',
    '- Aborted means the final lane is a terminal failure, cancellation, or `5e-escalated`. Backlog, ready, progress, review, completed, and archive records are not inferred as aborted.',
    '- Grade is read from orchestrator/review grade fields, with `code-review:grade-*` tags as a legacy fallback. Missing grades remain unknown.',
    `- Parse errors: ${report.dataQuality.parseErrors}. Other warnings: ${report.dataQuality.warningCount}. Excluded for missing model or thinking level: ${report.summary.taskRecordsExcludedMissingModelOrLevel}. Excluded without run evidence: ${report.summary.taskRecordsExcludedWithoutRunEvidence}.`,
    '',
  );
  return lines.join('\n');
}

async function writeAtomic(file, contents) {
  await mkdir(path.dirname(file), { recursive: true });
  const temporary = `${file}.tmp-${process.pid}`;
  await writeFile(temporary, contents, 'utf8');
  try {
    await rename(temporary, file);
  } finally {
    await rm(temporary, { force: true });
  }
}

async function main(argv = process.argv.slice(2)) {
  const options = parseArguments(argv);
  if (options.help) {
    console.log(usage());
    return;
  }
  if (!options.taskRoot && !options.snapshot) throw new Error(`No task source configured.\n\n${usage()}`);

  const loaded = options.snapshot
    ? await loadSnapshot(options.snapshot)
    : await loadTaskStore(options.taskRoot);
  const source = options.snapshot
    ? { mode: 'snapshot', label: options.sourceLabel ?? path.basename(options.snapshot) }
    : { mode: 'task-storage', label: options.sourceLabel ?? path.resolve(options.taskRoot), storageRootCount: loaded.roots.length };
  const report = aggregateTasks(loaded.tasks, { ...loaded, source });
  const outputDir = path.resolve(options.outputDir);
  await writeAtomic(path.join(outputDir, 'model-benchmark.json'), `${JSON.stringify(report, null, 2)}\n`);
  await writeAtomic(path.join(outputDir, 'model-benchmark.md'), renderMarkdown(report));
  console.log(`Aggregated ${report.summary.taskRecordsIncluded} tasks into ${report.summary.groupCount} groups.`);
  console.log(`Wrote ${path.join(outputDir, 'model-benchmark.json')}`);
  console.log(`Wrote ${path.join(outputDir, 'model-benchmark.md')}`);
}

export {
  aggregateTasks,
  loadTaskStore,
  normalizeTask,
  renderMarkdown,
};

if (process.argv[1]
    && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}

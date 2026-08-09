#!/usr/bin/env node

import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const FIXED_TIME_BASE = '2026-08-09T08:00:00.000Z';
const DEFAULT_OUTPUT = fileURLToPath(new URL('./pinned-seed.json', import.meta.url));
const LANE_ORDER = [
  '0-backlog',
  '1-preparation',
  '1b-needs-human-review',
  '2-ready',
  '3-progress',
  '4-auto-review',
  '5-human-review',
  '5e-escalated',
  '6-completed',
  '7-archive',
];

export function parseArgs(argv) {
  const args = { output: DEFAULT_OUTPUT };
  for (let index = 0; index < argv.length; index++) {
    const name = argv[index];
    if (name === '--source-root') args.sourceRoot = argv[++index];
    else if (name === '--project') args.project = argv[++index];
    else if (name === '--task') args.task = argv[++index];
    else if (name === '--secondary-project') args.secondaryProject = argv[++index];
    else if (name === '--output') args.output = argv[++index];
    else throw new Error(`Unknown argument: ${name}`);
  }
  for (const required of ['sourceRoot', 'project', 'task']) {
    if (!args[required]) throw new Error(`Missing required --${required.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`)}`);
  }
  return args;
}

function readTaskFolders(projectDir) {
  const tasksRoot = join(projectDir, 'tasks');
  if (!existsSync(tasksRoot)) throw new Error(`Task folder does not exist: ${tasksRoot}`);
  const tasks = [];
  for (const bucket of readdirSync(tasksRoot, { withFileTypes: true }).filter((entry) => entry.isDirectory())) {
    const bucketDir = join(tasksRoot, bucket.name);
    for (const taskEntry of readdirSync(bucketDir, { withFileTypes: true }).filter((entry) => entry.isDirectory())) {
      const folder = join(bucketDir, taskEntry.name);
      const taskJson = join(folder, 'task.json');
      if (!existsSync(taskJson)) continue;
      const data = JSON.parse(readFileSync(taskJson, 'utf8'));
      tasks.push({ folder, data, sourceKey: data.key || taskEntry.name });
    }
  }
  return tasks;
}

function findProjectDir(sourceRoot, project) {
  const direct = join(sourceRoot, 'projects', project);
  if (existsSync(direct)) return direct;
  const projectsRoot = join(sourceRoot, 'projects');
  if (!existsSync(projectsRoot)) throw new Error(`Workspace projects folder does not exist: ${projectsRoot}`);
  const normalized = project.trim().toLowerCase();
  const match = readdirSync(projectsRoot, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .find((entry) => entry.name.toLowerCase() === normalized);
  if (!match) throw new Error(`Project not found in workspace: ${project}`);
  return join(projectsRoot, match.name);
}

function sanitizedText(value, replacements) {
  let text = String(value ?? '');
  const longestNeedleFirst = [...replacements]
    .sort(([left], [right]) => String(right ?? '').length - String(left ?? '').length);
  for (const [needle, replacement] of longestNeedleFirst) {
    if (!needle) continue;
    text = text.replace(new RegExp(escapeRegExp(needle), 'gi'), replacement);
  }
  text = text
    .replace(/[A-Z]:[\\/][^\n\r)`"']+/gi, '<workspace-path>')
    .replace(/\/(?:home|Users)\/[^\n\r)`"']+/g, '<workspace-path>')
    .replace(/\b(?!(?:DEMO|PLAT)-)[A-Z][A-Z0-9]{1,12}-\d+\b/g, 'DEMO-TASK');
  return text;
}

function escapeRegExp(value) {
  return String(value).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function latestReview(folder) {
  const names = readdirSync(folder)
    .filter((name) => /^code-review-grade-.*\.md$/i.test(name))
    .sort()
    .reverse();
  if (names.length === 0) return null;
  return readFileSync(join(folder, names[0]), 'utf8');
}

function frontmatterValue(markdown, field) {
  if (!markdown) return null;
  const match = markdown.match(new RegExp(`^${field}:\\s*(.+)$`, 'im'));
  return match?.[1]?.trim().replace(/^['"]|['"]$/g, '') || null;
}

function findings(markdown, replacements) {
  if (!markdown) return [];
  return markdown
    .split('\n')
    .map((line) => line.match(/^\s*[-*]\s+(?:\*\*[^*]+:\*\*\s*)?(.+)/)?.[1]?.trim())
    .filter(Boolean)
    .map((line) => sanitizedText(line, replacements))
    .slice(0, 3);
}

function taskType(data) {
  const value = String(data.taskType || data.type || 'feature').toLowerCase();
  return ['feature', 'bug', 'chore'].includes(value) ? value : 'feature';
}

function stateOf(data) {
  const state = String(data.state || '0-backlog');
  return LANE_ORDER.includes(state) ? state : '0-backlog';
}

function sanitizedTitle(task, replacements, index) {
  const title = sanitizedText(task.data.title, replacements).trim();
  return title && !title.includes('<workspace-path>') ? title : `Pinned task ${index + 1}`;
}

export function buildSnapshot({ sourceRoot, project, task, secondaryProject }) {
  const primaryDir = findProjectDir(sourceRoot, project);
  const primaryTasks = readTaskFolders(primaryDir);
  const selected = primaryTasks.find((entry) => String(entry.sourceKey).toLowerCase() === task.toLowerCase());
  if (!selected) throw new Error(`Task not found in project ${project}: ${task}`);

  const secondaryDir = secondaryProject ? findProjectDir(sourceRoot, secondaryProject) : null;
  const secondaryTasks = secondaryDir ? readTaskFolders(secondaryDir) : [];
  const allSourceKeys = [...primaryTasks, ...secondaryTasks].map((entry) => String(entry.sourceKey));
  const replacements = [
    [resolve(sourceRoot), '<workspace-path>'],
    [sourceRoot, '<workspace-path>'],
    [project, 'Demo App'],
    [project.replace(/[-_]+/g, ' '), 'Demo App'],
    [secondaryProject, 'Demo Platform'],
    [secondaryProject?.replace(/[-_]+/g, ' '), 'Demo Platform'],
    ...allSourceKeys.map((key) => [key, key === selected.sourceKey ? 'DEMO-9' : 'DEMO-TASK']),
  ];

  const orderedPrimary = primaryTasks
    .filter((entry) => entry !== selected)
    .sort((a, b) => {
      const lane = LANE_ORDER.indexOf(stateOf(a.data)) - LANE_ORDER.indexOf(stateOf(b.data));
      if (lane !== 0) return lane;
      return String(a.sourceKey).localeCompare(String(b.sourceKey), 'en');
    });
  const tasks = [];
  let demoNumber = 1;
  for (const [index, entry] of orderedPrimary.entries()) {
    if (demoNumber === 9) demoNumber++;
    tasks.push({
      project: 'demo-app',
      key: `DEMO-${demoNumber++}`,
      state: stateOf(entry.data),
      type: taskType(entry.data),
      title: sanitizedTitle(entry, replacements, index),
      ...(existsSync(join(entry.folder, 'pipeline-execution.json')) ? { history: true } : {}),
    });
  }
  tasks.push({
    project: 'demo-app',
    key: 'DEMO-9',
    state: '5e-escalated',
    type: taskType(selected.data),
    title: sanitizedTitle(selected, replacements, orderedPrimary.length),
    history: true,
    decision: true,
  });
  for (const [index, entry] of secondaryTasks
    .sort((a, b) => String(a.sourceKey).localeCompare(String(b.sourceKey), 'en'))
    .entries()) {
    tasks.push({
      project: 'demo-platform',
      key: `PLAT-${index + 1}`,
      state: stateOf(entry.data),
      type: taskType(entry.data),
      title: sanitizedTitle(entry, replacements, index),
      ...(existsSync(join(entry.folder, 'pipeline-execution.json')) ? { history: true } : {}),
    });
  }

  const review = latestReview(selected.folder);
  const promptPath = join(selected.folder, 'prompt.md');
  const prompt = existsSync(promptPath) ? readFileSync(promptPath, 'utf8') : `# ${selected.data.title || 'Decision task'}\n`;
  const reviewFindings = findings(review, replacements);
  while (reviewFindings.length < 3) {
    reviewFindings.push([
      'Verify the remaining focused regression assertion.',
      'Confirm the visual proof in both themes.',
      'Keep the diff and evidence attached to the decision.',
    ][reviewFindings.length]);
  }

  return {
    schemaVersion: 1,
    fixedTimeBase: FIXED_TIME_BASE,
    source: {
      kind: 'anonymized-real-workspace-export',
      capturedAt: FIXED_TIME_BASE,
      note: 'Names, paths, project keys, task keys, repository references, and timestamps were sanitized before this snapshot was committed. Capture never reads the source workspace.',
    },
    projects: [
      { key: 'demo-app', name: 'Demo App' },
      ...(secondaryProject ? [{ key: 'demo-platform', name: 'Demo Platform' }] : []),
    ],
    tasks,
    decision: {
      taskKey: 'DEMO-9',
      promptMarkdown: sanitizedText(prompt, replacements),
      reviewSummary: sanitizedText(frontmatterValue(review, 'summary') || 'The delivery needs one bounded operator decision.', replacements),
      reviewFindings,
      decisionTitle: 'Release this delivery?',
      decisionQuestion: 'Should the delivery continue for one focused fix, be accepted as-is, or be aborted?',
      decisionContext: 'The implementation summary, diff, automated grade, and visual proof are attached to this task.',
      diffFiles: ['frontend/src/app/features/demo/delivery.ts', 'frontend/src/app/features/demo/delivery.spec.ts'],
    },
  };
}

export function writeSnapshot(output, snapshot) {
  mkdirSync(dirname(output), { recursive: true });
  writeFileSync(output, `${JSON.stringify(snapshot, null, 2)}\n`, 'utf8');
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const snapshot = buildSnapshot(args);
  writeSnapshot(resolve(args.output), snapshot);
  console.log(`Wrote sanitized pinned seed snapshot: ${resolve(args.output)}`);
  console.log(`  tasks: ${snapshot.tasks.length}`);
  console.log(`  decision task: ${snapshot.decision.taskKey}`);
  console.log('Review the sanitized diff before committing this explicit seed update.');
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) main();

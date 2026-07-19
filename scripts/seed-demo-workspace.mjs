#!/usr/bin/env node
// Idempotent seed for the slim DEV demo datastore (ADR-0056).
//
// Builds a small, reproducible TaskRepository the dev backend can point at
// instead of the heavy production workspace (~1300 tasks / ~650 MB). The
// generated store holds a handful of tasks per lane across two demo projects,
// a few of them with run / token history so the statistics views have data.
//
// Usage:
//   node scripts/seed-demo-workspace.mjs [--root <path>] [--force]
//
// Default root: C:\Projects\agent-taskboard-workspace-demo
//   (override with --root <path> or the ATP_DEMO_ROOT env var).
//
// Re-running RESETS the demo store to a clean, known stand: every path this
// script manages (projects/, .metadata/, the workspace-root usage / settings
// files) is removed and rewritten. It never touches anything else under the
// root, so an operator-added .git stays put. The registry under
// .metadata/projects.json is intentionally NOT written here — the backend
// seeds it from WatchPaths on first boot (ADR-0042); wiping it forces a fresh,
// deterministic registry on the next dev start.

import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const DEFAULT_ROOT = 'C:\\Projects\\agent-taskboard-workspace-demo';
const OWNER = 'local-default';

function parseArgs(argv) {
  const args = { root: process.env.ATP_DEMO_ROOT || DEFAULT_ROOT };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--root') args.root = argv[++i];
  }
  return args;
}

// Deterministic timestamps so a re-seed produces byte-identical files
// (true idempotency, no clock drift between runs).
const BASE = Date.parse('2026-06-01T08:00:00.000Z');
function iso(offsetMinutes) {
  return new Date(BASE + offsetMinutes * 60_000).toISOString();
}

// ---- Fixture definition -------------------------------------------------

const PROJECTS = [
  { key: 'demo-app', name: 'Demo App' },
  { key: 'demo-platform', name: 'Demo Platform' },
];

// One slim card per lane. `hist` marks the cards that carry run + token
// history (timeline, session events, pipeline execution, lastUsage).
const TASKS = [
  // Demo App — full lane spread (0-backlog .. 7-archive)
  { project: 'demo-app', key: 'DEMO-1', state: '0-backlog', type: 'feature', title: 'Add dark-mode toggle to the settings page' },
  { project: 'demo-app', key: 'DEMO-2', state: '1-preparation', type: 'feature', title: 'Design the onboarding wizard flow' },
  { project: 'demo-app', key: 'DEMO-3', state: '2-ready', type: 'chore', title: 'Bump frontend dependencies to latest minor' },
  { project: 'demo-app', key: 'DEMO-4', state: '3-progress', type: 'bug', title: 'Fix avatar upload failing on large images', hist: true },
  { project: 'demo-app', key: 'DEMO-5', state: '4-auto-review', type: 'feature', title: 'Add CSV export to the reports table', hist: true },
  { project: 'demo-app', key: 'DEMO-6', state: '5-human-review', type: 'bug', title: 'Correct timezone handling in the calendar view' },
  { project: 'demo-app', key: 'DEMO-7', state: '6-completed', type: 'feature', title: 'Introduce keyboard shortcuts for the board', hist: true },
  { project: 'demo-app', key: 'DEMO-8', state: '7-archive', type: 'chore', title: 'Retire the legacy notifications banner', hist: true },
  // Demo Platform — a lighter spread
  { project: 'demo-platform', key: 'PLAT-1', state: '0-backlog', type: 'feature', title: 'Expose a health-check endpoint' },
  { project: 'demo-platform', key: 'PLAT-2', state: '2-ready', type: 'chore', title: 'Add structured request logging' },
  { project: 'demo-platform', key: 'PLAT-3', state: '6-completed', type: 'feature', title: 'Cache catalog responses for 60s', hist: true },
  { project: 'demo-platform', key: 'PLAT-4', state: '7-archive', type: 'bug', title: 'Stop double-counting retried jobs in metrics' },
];

// ---- Writers ------------------------------------------------------------

function writeJson(path, obj) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify(obj, null, 2) + '\n', 'utf8');
}

function writeText(path, text) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, text, 'utf8');
}

function writeJsonl(path, rows) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, rows.map((r) => JSON.stringify(r)).join('\n') + '\n', 'utf8');
}

function bucket(key) {
  const n = parseInt(key.split('-').pop(), 10) || 0;
  return String(Math.floor(n / 1000)).padStart(3, '0');
}

function slug(task) {
  return task.title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');
}

function taskDir(root, task) {
  return join(root, 'projects', task.project, 'tasks', bucket(task.key), task.key);
}

function writeTask(root, task, index) {
  const dir = taskDir(root, task);
  const id = slug(task);
  const created = iso(index * 30);
  const entered = iso(index * 30 + 120);

  const json = {
    id,
    title: task.title,
    createdAt: created,
    enteredLaneAt: entered,
    state: task.state,
    order: (index + 1) * 10,
    agent: 'claude',
    ownerClientId: OWNER,
    model: 'claude-opus-4-8',
    thinkingLevel: 'medium',
    cliType: 'claude',
    kind: 'task',
    mode: 'coding',
    allowWebAccess: false,
    taskType: task.type,
    key: task.key,
  };

  if (task.hist) {
    json.sessionName = `demo-session-${task.key.toLowerCase()}`;
    json.lastProgressAt = iso(index * 30 + 110);
    json.lastUsage = {
      At: iso(index * 30 + 110),
      Tokens: null,
      Changes: '+128 -34',
      Requests: '2 Premium (3m 54s)',
    };
    if (task.key === 'DEMO-5') {
      json.tags = ['demo', 'frontend', 'code-review:concerns', 'code-review:grade-b'];
    }
  }

  writeJson(join(dir, 'task.json'), json);
  writeText(
    join(dir, 'prompt.md'),
    `# ${task.title}\n\nDemo task seeded by scripts/seed-demo-workspace.mjs for the slim DEV demo store (ADR-0056). Not real work — safe to edit, move, or delete; a re-seed restores it.\n`
  );

  if (task.hist) writeHistory(dir, task, id, index);
  if (task.key === 'DEMO-5') writeReviewEvidence(dir);
}

function writeReviewEvidence(dir) {
  writeText(
    join(dir, 'code-review-2026-06-01T12-00-00.md'),
    `---
type: code-review-grade
runAt: ${iso(260)}
model: claude-haiku-4-5
cliType: claude
commit: demo000000000000000000000000000000000000
grade: B
verdict: concerns
summary: Export flow is ready for human review; add one empty-state assertion before release.
tag: code-review:grade-b
---

# Code Review - Quality Grade: B

> Export flow is ready for human review; add one empty-state assertion before release.

## Findings

- **Medium:** Add an assertion for exporting an empty report table.
- **Verified:** CSV escaping and timezone formatting have deterministic coverage.
`
  );
}

function writeHistory(dir, task, id, index) {
  const t0 = iso(index * 30 + 100);
  const t1 = iso(index * 30 + 104);
  const t2 = iso(index * 30 + 110);
  const sha = 'demo000000000000000000000000000000000000';

  writeJsonl(join(dir, 'logs', 'timeline.jsonl'), [
    { ts: t0, kind: 'prompt_created', actor: `human:${OWNER}`, payloadRef: 'prompt.md', summary: `Task created: ${task.title}`, details: { targetState: '0-backlog', agent: 'claude' } },
    { ts: t1, kind: 'agent_run_started', actor: 'system', summary: 'claude CLI start', details: { cli: 'claude', intent: 'start', resumed: 'false' } },
    { ts: t2, kind: 'agent_run_finished', actor: 'agent', summary: 'claude run finished in 354,2s', details: { cli: 'claude', status: 'completed' } },
  ]);

  writeJsonl(join(dir, 'logs', 'session-events.jsonl'), [
    { Ts: t1, Kind: 'start', Cli: 'claude', InputSessionId: null, CapturedSessionId: `demo-session-${task.key.toLowerCase()}`, Resumed: false, Reason: null, HeadShaBefore: sha, HeadShaAfter: sha, ContextRef: 'logs/run-context/run-demo.md' },
  ]);

  writeJson(join(dir, 'pipeline-execution.json'), {
    pipelineId: 'standard-task-pipeline',
    pipelineVersion: 1,
    jobId: id,
    project: PROJECTS.find((p) => p.key === task.project).name,
    startedAt: t1,
    steps: [
      { stepId: 'core-agent-run', kind: 1, model: 'claude-opus-4-8', status: 2, startedAt: t1, completedAt: t2, durationMs: 354200, inputTokens: 42827, outputTokens: 30668, cacheReadTokens: 2284920, cacheCreationTokens: 341824 },
      { stepId: 'aspect-code-quality', kind: 2, model: 'claude-haiku-4-5', status: 2, startedAt: t2, completedAt: t2, durationMs: 8200, inputTokens: 1200, outputTokens: 540, cacheReadTokens: 18000, cacheCreationTokens: 0 },
    ],
  });
}

function writeWorkspaceRootFiles(root) {
  // Token history for the global statistics views.
  const usageRows = [];
  const sources = ['prompt-enhancement', 'title-generation', 'task-classification', 'summary-generation'];
  for (let i = 0; i < 8; i++) {
    usageRows.push({
      ts: iso(i * 45),
      source: sources[i % sources.length],
      model: 'claude-haiku-4-5',
      inputTokens: 9 + i,
      outputTokens: 500 + i * 23,
      cacheReadTokens: 27913,
      cacheCreationTokens: 6000 + i * 40,
      durationMs: 7000 + i * 300,
      ok: true,
    });
  }
  writeJsonl(join(root, 'adhoc-usage.jsonl'), usageRows);

  // Per-project runtime settings: keep the demo projects on manual so a dev
  // backend brought up for debugging never auto-runs the demo cards.
  const settings = {};
  for (const p of PROJECTS) {
    settings[p.name] = {
      AutoCommit: false,
      AutoPushStrategy: 'off',
      RunnerMode: 'manual',
      OrchestratorModel: null,
      OrchestratorThinkingLevel: null,
      AnalysisSchedules: null,
      AutonomyLevel: null,
      IntakeEnabled: null,
      LaneSortStrategyOverrides: null,
      PipelineSteps: null,
      MaxParallelism: 1,
      IntegrationBranch: 'develop',
      IntegrationStrategy: 'direct-merge',
      CliModes: null,
      EpicPlanningModel: null,
      EpicPlanningThinkingLevel: null,
      EpicSubTasksToReady: null,
    };
  }
  writeJson(join(root, 'project-settings.json'), settings);

  writeJson(join(root, 'tags.json'), {
    Version: 1,
    Tags: [
      { Name: 'demo', Color: '#a78bfa' },
      { Name: 'frontend', Color: '#60a5fa' },
      { Name: 'platform', Color: '#34d399' },
    ],
  });

  writeText(
    join(root, 'README.md'),
    '# Demo TaskRepository (generated)\n\nSlim, reproducible datastore for the DEV backend (ADR-0056). Generated by\n`scripts/seed-demo-workspace.mjs` in the agent-taskboard repo. Do not hand-edit\nfor anything you want to keep — re-running the seed resets this store to a\nclean stand.\n'
  );
}

function writePresentationStory(root) {
  const demoApp = join(root, 'projects', 'demo-app');
  writeText(
    join(demoApp, 'README.md'),
    '# Demo App\n\nA deterministic sample product used only for Agent Studio demonstrations.\n'
  );
  writeText(
    join(demoApp, 'docs', 'architecture.md'),
    '# Architecture\n\nThe demo has an Angular client, an API boundary, and a review pipeline. Agent Studio keeps tasks, execution evidence, and project knowledge in one operator workspace.\n'
  );
  writeJsonl(join(demoApp, '.orchestrator', 'orchestrator-chat.jsonl'), [
    { id: 'demo-chat-01', ts: iso(210), role: 'user', text: 'What should we show in the MVP walkthrough?' },
    { id: 'demo-chat-02', ts: iso(211), role: 'orchestrator', text: 'Start with the cross-lane board, open DEMO-5 to connect execution with review evidence, then finish in project knowledge and token usage.' },
    { id: 'demo-chat-03', ts: iso(212), role: 'user', text: 'Keep the demo safe and repeatable.' },
    { id: 'demo-chat-04', ts: iso(213), role: 'orchestrator', text: 'Confirmed. This workspace contains seeded demo data only and can be reset before every capture.' },
  ]);
}

// ---- Idempotent reset + run --------------------------------------------

function reset(root) {
  // Remove only the paths this seed owns; leave an operator-added .git etc.
  for (const managed of ['projects', '.metadata', 'adhoc-usage.jsonl', 'project-settings.json', 'tags.json', 'README.md']) {
    rmSync(join(root, managed), { recursive: true, force: true });
  }
}

function main() {
  const { root } = parseArgs(process.argv.slice(2));
  const productionGuard = root.replace(/[\\/]+$/, '').toLowerCase();
  if (productionGuard.endsWith('agent-taskboard-workspace')) {
    console.error(`Refusing to seed the production workspace: ${root}`);
    console.error('Pass a separate --root (e.g. ...\\agent-taskboard-workspace-demo).');
    process.exit(1);
  }

  mkdirSync(root, { recursive: true });
  reset(root);

  TASKS.forEach((task, i) => writeTask(root, task, i));
  writeWorkspaceRootFiles(root);
  writePresentationStory(root);

  const perLane = TASKS.reduce((acc, t) => ((acc[t.state] = (acc[t.state] || 0) + 1), acc), {});
  console.log(`Seeded demo store at: ${root}`);
  console.log(`  projects: ${PROJECTS.map((p) => p.name).join(', ')}`);
  console.log(`  tasks:    ${TASKS.length} (${TASKS.filter((t) => t.hist).length} with run/token history)`);
  console.log(`  lanes:    ${Object.keys(perLane).sort().join(', ')}`);
  console.log('Registry (.metadata) is left to the backend to seed from WatchPaths on first boot (ADR-0042).');
}

const __isMain = process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1];
if (__isMain) main();

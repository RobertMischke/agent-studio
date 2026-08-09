#!/usr/bin/env node
// Idempotent seed for the slim DEV demo datastore (ADR-0056).
//
// Builds a small, reproducible TaskRepository the dev backend can point at
// instead of the heavy production workspace (~1300 tasks / ~650 MB). The
// generated store is built from the committed, sanitized pinned snapshot and
// holds tasks per lane across two demo projects. A subset has run / token
// history so the statistics views have data.
//
// Usage:
//   node scripts/seed-demo-workspace.mjs [--root <path>] [--force]
//
// Default root: C:\Projects\agent-taskboard-workspace-demo
//   (override with --root <path> or the ATP_DEMO_ROOT env var).
//
// Re-running RESETS the demo store to a clean, known stand: every path this
// script manages (projects/, .metadata/, logs/, and the workspace-root usage /
// settings files) is removed and rewritten. It never touches anything else under the
// root, so an operator-added .git stays put. The registry under
// .metadata/projects.json is intentionally NOT written here — the backend
// seeds it from WatchPaths on first boot (ADR-0042); wiping it forces a fresh,
// deterministic registry on the next dev start.

import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, utimesSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync } from 'node:zlib';

const DEFAULT_ROOT = 'C:\\Projects\\agent-taskboard-workspace-demo';
const OWNER = 'local-default';

function parseArgs(argv) {
  const args = { root: process.env.ATP_DEMO_ROOT || DEFAULT_ROOT };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--root') args.root = argv[++i];
  }
  return args;
}

const PINNED_SNAPSHOT_PATH = fileURLToPath(new URL('./presentation-capture/pinned-seed.json', import.meta.url));
const PINNED_SNAPSHOT = JSON.parse(readFileSync(PINNED_SNAPSHOT_PATH, 'utf8'));

// Deterministic timestamps so a re-seed produces byte-identical files and the
// UI never derives capture-visible dates from a re-seed's wall clock.
const BASE = Date.parse(PINNED_SNAPSHOT.fixedTimeBase);
function iso(offsetMinutes) {
  return new Date(BASE + offsetMinutes * 60_000).toISOString();
}

// ---- Fixture definition -------------------------------------------------

const PROJECTS = PINNED_SNAPSHOT.projects;
const TASKS = PINNED_SNAPSHOT.tasks;
const DECISION = PINNED_SNAPSHOT.decision;

// ---- Writers ------------------------------------------------------------

function writeJson(path, obj) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify(obj, null, 2) + '\n', 'utf8');
  stamp(path);
}

function writeText(path, text) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, text, 'utf8');
  stamp(path);
}

function writeJsonl(path, rows) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, rows.map((r) => JSON.stringify(r)).join('\n') + '\n', 'utf8');
  stamp(path);
}

function writeBuffer(path, body) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, body);
  stamp(path);
}

function stamp(path) {
  const at = new Date(BASE);
  utimesSync(path, at, at);
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
    order: task.decision ? 1 : (index + 1) * 10,
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
    noBranchExpected: task.state === '6-completed' || task.state === '7-archive',
  };

  if (task.history) {
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

  if (task.decision) {
    json.tags = ['demo', 'frontend', 'code-review:concerns', 'code-review:grade-b'];
    json.commits = [
      {
        sha: 'd3e0c9f84a1b7e62551a7a51c3b84cf6d2a09871',
        shortSha: 'd3e0c9f8',
        message: 'feat(reports): add bounded CSV export',
        filesChanged: DECISION.diffFiles.length,
        files: DECISION.diffFiles,
        at: iso(274),
        attribution: 'task-key',
        attributionConfidence: 'high'
      }
    ];
  }

  writeJson(join(dir, 'task.json'), json);
  writeText(join(dir, 'prompt.md'), task.decision
    ? DECISION.promptMarkdown
    : `# ${task.title}\n\nPinned demo task seeded by scripts/seed-demo-workspace.mjs for the slim DEV demo store (ADR-0056). The data is sanitized and safe to reset; a re-seed restores the exact captured state.\n`);

  if (task.history) writeHistory(dir, task, id, index);
  if (task.key === 'DEMO-5') {
    writeReviewEvidence(dir);
    writeArtifactGalleryScene(dir);
  }
  if (task.decision) writeDecisionState(dir, task, id);
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

function writeArtifactGalleryScene(dir) {
  const images = [
    ['gallery-dashboard-light.png', 0],
    ['gallery-dashboard-dark.png', 1],
    ['gallery-export-dialog.png', 2],
    ['gallery-empty-state.png', 3],
    ['gallery-filter-panel.png', 4],
    ['gallery-mobile-table.png', 5],
    ['gallery-review-light.png', 6],
    ['gallery-review-dark.png', 7],
  ];
  for (const [fileName, variant] of images) {
    writeBuffer(join(dir, 'results', fileName), createGalleryPng(720, 450, variant));
  }

  writeText(
    join(dir, 'results', 'delivery.diff'),
    `diff --git a/frontend/src/app/reports/export.ts b/frontend/src/app/reports/export.ts
index 6a70b17..d3e0c9f 100644
--- a/frontend/src/app/reports/export.ts
+++ b/frontend/src/app/reports/export.ts
@@ -18,0 +19,5 @@
+export function escapeCsvCell(value: string): string {
+  const normalized = value.replaceAll('"', '""');
+  return /[",\\n]/.test(normalized) ? '"' + normalized + '"' : normalized;
+}
`
  );
  writeText(
    join(dir, 'results', 'gallery-notes.md'),
    `# Gallery review notes

The CSV export scene is pinned for the ADR-0056 presentation flow.

- Light and dark views use the same deterministic report fixture.
- The dialog, empty state, filter panel, and mobile table are included.
- The delivery diff below the image grid is ready for operator review.
`
  );
  writeJson(join(dir, 'results', 'gallery-metrics.json'), {
    scene: 'activity-artifact-gallery',
    pinned: true,
    imageCount: images.length,
    themes: ['light', 'dark'],
    generatedAt: iso(260),
  });
  writeText(
    join(dir, 'results', 'gallery-run.log'),
    `[12:20:00.000] capture light theme: ok
[12:20:04.000] capture dark theme: ok
[12:20:05.000] artifact manifest: pinned
`
  );

  const artifactMessage = [
    'The pinned report-gallery evidence is ready for presentation:',
    ...images.map(([fileName]) => `- [${fileName}](results/${fileName})`),
    '- [Delivery diff](results/delivery.diff)',
    '- [Gallery notes](results/gallery-notes.md)',
    '- [Gallery metrics](results/gallery-metrics.json)',
    '- [Capture log](results/gallery-run.log)',
  ].join('\n');
  const frame = JSON.stringify({
    type: 'item.completed',
    item: { id: 'demo-artifact-gallery', type: 'agent_message', text: artifactMessage },
  });
  writeText(
    join(dir, 'logs', 'cli-output.log'),
    `[12:20:06.000] [stdout] ${frame}\n[12:20:07.000] [stdout] [[TASK_DONE]]\n`,
  );
}

function writeDecisionState(dir, task, id) {
  const reviewFile = 'code-review-grade-2026-08-09T12-32-00Z.md';
  writeText(
    join(dir, reviewFile),
    `---
type: code-review-grade
runAt: ${iso(272)}
model: gpt-5.4-mini
cliType: codex
thinkingLevel: medium
commit: d3e0c9f84a1b7e62551a7a51c3b84cf6d2a09871
grade: B
verdict: concerns
summary: ${DECISION.reviewSummary}
tag: code-review:grade-b
---

# Code Review - Quality Grade: B

> ${DECISION.reviewSummary}

## Findings

${DECISION.reviewFindings.map((finding) => `- **Medium:** ${finding}`).join('\n')}

## Verified

- CSV escaping and timezone formatting passed focused unit coverage.
- The attached browser capture was reviewed in light and dark themes.
`
  );

  const assessments = DECISION.reviewFindings.map((finding) => ({
    finding,
    action: 'Escalate',
    reason: 'The bounded automatic review budget is exhausted; the operator owns the release decision.'
  }));
  const councilReaction = {
    createdAt: iso(273),
    reviewFileName: reviewFile,
    grade: 'B',
    disposition: 'Escalate',
    summary: `Escalate ${assessments.length} open review findings; loop budget exhausted.`,
    assessments,
    startsNewRound: false,
    targetJobId: null,
    targetRunAttempt: null
  };
  writeJson(join(dir, `${reviewFile}.council-reaction.json`), councilReaction);

  writeText(
    join(dir, 'orchestrator-follow-up.md'),
    `# Operator decision handoff

The delivery is bounded and reviewable. Inspect the attached diff, browser proof, and Grade B verdict, then choose whether to request the one focused assertion, accept as-is, or abort.

${DECISION.reviewFindings.map((finding) => `- ${finding}`).join('\n')}
`
  );
  writeText(
    join(dir, 'status.md'),
    `# Status
Result: Needs decision
Case: review
Model: codex / gpt-5.4

## Overview
The reports export is implemented and verified. One bounded empty-state assertion remains for the operator to accept or return.

## What Was Done
- Added deterministic CSV escaping and timezone formatting.
- Attached the delivery diff, focused verification, and browser proof.
- Recorded the Grade B review and explicit escalation verdict.

## Tests
- Focused unit tests: 18 passed.
- Playwright: light and dark evidence captured.

## Open Items
- Decide whether the empty-table assertion blocks release.

## Images
- ![](../results/reports-export--real.png) (source: real)
`
  );
  writeJson(join(dir, 'aspect-tests-and-evidence.json'), {
    schemaVersion: 1,
    aspect: 'tests-and-evidence',
    verdict: 'concerns',
    summary: 'Focused tests passed; one explicit empty-table regression assertion remains for operator review.',
    findings: DECISION.reviewFindings,
    evidence: ['results/reports-export--real.png', 'results/delivery.diff']
  });

  const screenshot = createEvidencePng(720, 405);
  writeBuffer(join(dir, 'results', 'reports-export--real.png'), screenshot);
  writeBuffer(join(dir, 'attachments', 'export-layout.png'), screenshot);
  writeText(
    join(dir, 'results', 'delivery.diff'),
    `diff --git a/${DECISION.diffFiles[0]} b/${DECISION.diffFiles[0]}
index 6a70b17..d3e0c9f 100644
--- a/${DECISION.diffFiles[0]}
+++ b/${DECISION.diffFiles[0]}
@@ -18,0 +19,4 @@
+export function escapeCsvCell(value: string): string {
+  const normalized = value.replaceAll('"', '""');
+  return /[",\\n]/.test(normalized) ? '"' + normalized + '"' : normalized;
+}
diff --git a/${DECISION.diffFiles[1]} b/${DECISION.diffFiles[1]}
index 0b15ad0..d3e0c9f 100644
--- a/${DECISION.diffFiles[1]}
+++ b/${DECISION.diffFiles[1]}
@@ -11,0 +12,2 @@
+it('escapes commas and quotes', () => expect(exportRow(fixture)).toMatchSnapshot());
+it.todo('keeps the empty-table export disabled');
`
  );
  writeJsonl(join(dir, 'results', 'review-evidence.jsonl'), [
    {
      id: 'review-empty-table-assertion',
      source: 'code-review',
      severity: 'warn',
      title: 'One focused empty-table assertion remains',
      body: DECISION.reviewFindings[0],
      createdAt: iso(272),
      runIndex: 1,
      artifacts: ['results/reports-export--real.png', 'results/delivery.diff'],
      fileRefs: [`${DECISION.diffFiles[1]}:13`],
      acknowledged: false,
      followupJobId: null
    },
    {
      id: 'browser-proof-both-themes',
      source: 'task-check',
      severity: 'info',
      title: 'Browser proof attached in both themes',
      body: 'The export affordance, review state, and evidence surface remain readable in light and dark themes.',
      createdAt: iso(271),
      runIndex: 1,
      artifacts: ['results/reports-export--real.png'],
      fileRefs: [DECISION.diffFiles[0]],
      acknowledged: true,
      followupJobId: null
    }
  ]);
  writeJson(join(dir, 'results', 'decision.json'), {
    version: 1,
    id: 'reports-export-release',
    title: DECISION.decisionTitle,
    question: DECISION.decisionQuestion,
    context: DECISION.decisionContext,
    recommendation: {
      optionId: 'request-assertion',
      reason: 'Request the one focused assertion, then return directly to this release decision.'
    },
    options: [
      {
        id: 'request-assertion',
        label: 'Request one assertion',
        summary: 'Return the task for the named empty-table regression test only.',
        consequences: ['Keeps the current implementation.', 'Adds one focused run before release.'],
        action: { kind: 'steer', prompt: 'Add only the missing empty-table export assertion, run the focused tests, and preserve the attached evidence.' }
      },
      {
        id: 'accept-as-is',
        label: 'Accept as-is',
        summary: 'Ship the reviewed implementation without another run.',
        consequences: ['The existing coverage remains the release boundary.', 'The open finding is recorded as accepted risk.'],
        action: { kind: 'move', targetState: '6-completed' }
      },
      {
        id: 'abort-release',
        label: 'Abort release',
        summary: 'Archive the delivery and keep its evidence for later.',
        consequences: ['No export reaches release.', 'The task remains auditable in Archive.'],
        action: { kind: 'move', targetState: '7-archive' }
      }
    ],
    steer: {
      label: 'Additional guidance',
      placeholder: 'Optional constraints for the selected path',
      required: false
    }
  });

  writeJsonl(join(dir, 'logs', 'timeline.jsonl'), [
    { ts: iso(250), kind: 'prompt_created', actor: `human:${OWNER}`, payloadRef: 'prompt.md', summary: `Task created: ${task.title}`, details: { targetState: '0-backlog', agent: 'codex' } },
    { ts: iso(252), kind: 'agent_run_started', actor: 'system', summary: 'codex CLI start', details: { cli: 'codex', intent: 'start', resumed: 'false' } },
    { ts: iso(270), kind: 'agent_run_finished', actor: 'agent', summary: 'codex run completed', details: { cli: 'codex', status: 'completed' } },
    { ts: iso(272), kind: 'code_review_grade_completed', actor: 'review', summary: 'Quality grade B with one focused gap', details: { grade: 'B', verdict: 'concerns', reviewFile } },
    { ts: iso(273), kind: 'orchestrator_escalated', actor: 'orchestrator', summary: 'Escalated for operator decision', details: { reason: '[review-loop-budget-exhausted] One focused assertion remains.', cause: 'completion-gate', attempt: '3', maxAttempts: '3' } }
  ]);
}

function createEvidencePng(width, height) {
  const rowSize = width * 4 + 1;
  const raw = Buffer.alloc(rowSize * height);
  const fill = (x, y, w, h, color) => {
    for (let yy = Math.max(0, y); yy < Math.min(height, y + h); yy++) {
      for (let xx = Math.max(0, x); xx < Math.min(width, x + w); xx++) {
        const offset = yy * rowSize + 1 + xx * 4;
        raw[offset] = color[0];
        raw[offset + 1] = color[1];
        raw[offset + 2] = color[2];
        raw[offset + 3] = color[3] ?? 255;
      }
    }
  };
  fill(0, 0, width, height, [20, 23, 31, 255]);
  fill(28, 24, width - 56, 42, [37, 43, 57, 255]);
  fill(48, 38, 142, 13, [119, 165, 255, 255]);
  fill(28, 88, 184, height - 116, [29, 34, 45, 255]);
  fill(232, 88, width - 260, height - 116, [29, 34, 45, 255]);
  for (let index = 0; index < 5; index++) {
    fill(48, 112 + index * 47, 132, 12, index === 2 ? [87, 214, 185, 255] : [105, 115, 137, 255]);
    fill(252, 112 + index * 47, width - 320, 12, [77 + index * 6, 88 + index * 6, 108 + index * 5, 255]);
    fill(width - 92, 108 + index * 47, 34, 20, index === 2 ? [44, 118, 99, 255] : [50, 57, 72, 255]);
  }
  for (let y = 0; y < height; y++) raw[y * rowSize] = 0;
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', deflateSync(raw, { level: 9 })),
    pngChunk('IEND', Buffer.alloc(0))
  ]);
}

function createGalleryPng(width, height, variant) {
  const rowSize = width * 4 + 1;
  const raw = Buffer.alloc(rowSize * height);
  const palettes = [
    [[241, 244, 249, 255], [255, 255, 255, 255], [62, 99, 221, 255]],
    [[20, 23, 31, 255], [31, 36, 48, 255], [119, 165, 255, 255]],
    [[239, 243, 250, 255], [255, 255, 255, 255], [33, 161, 121, 255]],
    [[24, 28, 38, 255], [36, 42, 56, 255], [238, 178, 70, 255]],
  ];
  const [background, surface, accent] = palettes[variant % palettes.length];
  const fill = (x, y, w, h, color) => {
    for (let yy = Math.max(0, y); yy < Math.min(height, y + h); yy++) {
      for (let xx = Math.max(0, x); xx < Math.min(width, x + w); xx++) {
        const offset = yy * rowSize + 1 + xx * 4;
        raw[offset] = color[0];
        raw[offset + 1] = color[1];
        raw[offset + 2] = color[2];
        raw[offset + 3] = color[3];
      }
    }
  };
  fill(0, 0, width, height, background);
  fill(24, 22, width - 48, 42, surface);
  fill(44, 37, 118 + variant * 7, 12, accent);
  fill(24, 84, 164, height - 108, surface);
  fill(208, 84, width - 232, height - 108, surface);
  for (let row = 0; row < 5; row++) {
    const tone = 90 + ((variant + row) % 4) * 18;
    fill(228, 112 + row * 53, width - 316, 13, [tone, tone + 7, tone + 17, 255]);
    fill(width - 72, 107 + row * 53, 30, 22, row === variant % 5 ? accent : [82, 91, 108, 255]);
  }
  fill(48, 112 + (variant % 5) * 53, 112, 13, accent);
  for (let y = 0; y < height; y++) raw[y * rowSize] = 0;

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', deflateSync(raw, { level: 9 })),
    pngChunk('IEND', Buffer.alloc(0)),
  ]);
}

function pngChunk(type, data) {
  const typeBytes = Buffer.from(type, 'ascii');
  const chunk = Buffer.concat([typeBytes, data]);
  const output = Buffer.alloc(data.length + 12);
  output.writeUInt32BE(data.length, 0);
  chunk.copy(output, 4);
  output.writeUInt32BE(crc32(chunk), data.length + 8);
  return output;
}

function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return (crc ^ 0xffffffff) >>> 0;
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

function writeDecisionJournal(root) {
  const task = TASKS.find((candidate) => candidate.key === DECISION.taskKey);
  if (!task) throw new Error(`Pinned decision task is missing: ${DECISION.taskKey}`);
  const id = slug(task);
  const reviewFileName = 'code-review-grade-2026-08-09T12-32-00Z.md';
  writeJsonl(join(root, 'logs', 'decisions', 'Demo App.jsonl'), [{
    createdAt: iso(273),
    jobId: id,
    project: 'Demo App',
    kind: 'Escalate',
    reason: '[review-loop-budget-exhausted] One focused assertion remains for the operator.',
    prompt: 'Review the attached Grade B verdict and choose the bounded release path.',
    response: 'The implementation is reviewable and one focused test gap remains. [[DECISION:ESCALATE]]',
    followUp: '',
    attemptChainId: 'demo-release-chain-1',
    gateId: 'demo-release-decision',
    subjectSha: 'd3e0c9f84a1b7e62551a7a51c3b84cf6d2a09871',
    failureFingerprint: 'empty-table-assertion',
    failureKind: 'review-loop-budget-exhausted',
    councilReaction: {
      createdAt: iso(273),
      reviewFileName,
      grade: 'B',
      disposition: 'Escalate',
      summary: `Escalate ${DECISION.reviewFindings.length} open review findings; loop budget exhausted.`,
      assessments: DECISION.reviewFindings.map((finding) => ({
        finding,
        action: 'Escalate',
        reason: 'The bounded automatic review budget is exhausted; the operator owns the release decision.'
      })),
      startsNewRound: false,
      targetJobId: null,
      targetRunAttempt: null
    },
    attemptEpoch: 0
  }]);
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

function writePinnedRepositoryMarker(projectRoot) {
  const gitDir = join(projectRoot, '.git');
  if (!existsSync(gitDir)) {
    mkdirSync(join(gitDir, 'objects'), { recursive: true });
    mkdirSync(join(gitDir, 'refs', 'heads'), { recursive: true });
    writeText(join(gitDir, 'HEAD'), 'ref: refs/heads/main\n');
    writeText(
      join(gitDir, 'config'),
      '[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n\tlogallrefupdates = false\n'
    );
  }
  writeText(join(projectRoot, '.gitignore'), 'tasks/\n.orchestrator/\n');
}

function writePresentationStory(root) {
  const demoApp = join(root, 'projects', 'demo-app');
  const demoPlatform = join(root, 'projects', 'demo-platform');
  writePinnedRepositoryMarker(demoApp);
  writePinnedRepositoryMarker(demoPlatform);
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
  for (const managed of ['projects', '.metadata', 'logs', 'adhoc-usage.jsonl', 'project-settings.json', 'tags.json', 'README.md']) {
    rmSync(join(root, managed), { recursive: true, force: true });
  }
}

function stampTree(root) {
  if (!existsSync(root)) return;
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) stampTree(path);
    stamp(path);
  }
  stamp(root);
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
  writeDecisionJournal(root);
  stampTree(root);

  const perLane = TASKS.reduce((acc, t) => ((acc[t.state] = (acc[t.state] || 0) + 1), acc), {});
  console.log(`Seeded demo store at: ${root}`);
  console.log(`  projects: ${PROJECTS.map((p) => p.name).join(', ')}`);
  console.log(`  tasks:    ${TASKS.length} (${TASKS.filter((t) => t.history).length} with run/token history)`);
  console.log(`  pinned:   ${DECISION.taskKey} carries review, diff, screenshot, evidence, and decision state`);
  console.log(`  lanes:    ${Object.keys(perLane).sort().join(', ')}`);
  console.log('Registry (.metadata) is left to the backend to seed from WatchPaths on first boot (ADR-0042).');
}

const __isMain = process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1];
if (__isMain) main();

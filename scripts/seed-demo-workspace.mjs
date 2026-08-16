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

import { createHash } from 'node:crypto';
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

// Versioned sidecar next to the sanitized snapshot. It carries the invented
// Wiki trees and the Dossier gallery, which are authored fixtures rather than
// sanitized real data, so the sanitizing export never rewrites them.
const PINNED_CONTENT_PATH = fileURLToPath(new URL('./presentation-capture/pinned-demo-content.json', import.meta.url));
const PINNED_CONTENT = JSON.parse(readFileSync(PINNED_CONTENT_PATH, 'utf8'));

// The Dossier gallery renders in the canonical house style. Reusing the
// committed article template keeps one stylesheet instead of a seed-local copy.
const ARTICLE_TEMPLATE_PATH = fileURLToPath(new URL('../docs/app/templates/article-document-v2.html', import.meta.url));

// Deterministic timestamps so a re-seed produces byte-identical files and the
// UI never derives capture-visible dates from a re-seed's wall clock.
const BASE = Date.parse(PINNED_SNAPSHOT.fixedTimeBase);
function iso(offsetMinutes) {
  return new Date(BASE + offsetMinutes * 60_000).toISOString();
}

// Dossier descriptors store second-precision UTC timestamps. The catalogue also
// accepts the millisecond form iso() returns, but every committed descriptor in
// the repository uses this shape, so the seeded ones match it.
function lifecycleIso(offsetMinutes) {
  return `${iso(offsetMinutes).slice(0, 19)}Z`;
}

// ---- Fixture definition -------------------------------------------------

const PROJECTS = PINNED_SNAPSHOT.projects;
const TASKS = PINNED_SNAPSHOT.tasks;
const DECISION = PINNED_SNAPSHOT.decision;
const WIKI = PINNED_CONTENT.wiki;
const DOSSIERS = PINNED_CONTENT.dossiers;
const TASK_WORKBENCH_REFERENCES = PINNED_CONTENT.taskWorkbenchReferences;

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

  // references.workbenches is the canonical card-to-Dossier edge; the
  // descriptor keys stay populated as the current compatibility bridge.
  const workbenches = TASK_WORKBENCH_REFERENCES[task.key];
  if (workbenches) {
    json.references = { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [], workbenches };
  }

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
    '# Demo TaskRepository (generated)\n\nSlim, reproducible datastore for the DEV backend (ADR-0056). Generated by\n`scripts/seed-demo-workspace.mjs` in the product repository. Do not hand-edit\nfor anything you want to keep; re-running the seed resets this store to a\nclean stand.\n'
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
    join(demoPlatform, 'README.md'),
    '# Demo Platform\n\nA deterministic sample supporting service used only for Agent Studio demonstrations.\n'
  );
  writeJsonl(join(demoApp, '.orchestrator', 'orchestrator-chat.jsonl'), [
    { id: 'demo-chat-01', ts: iso(210), role: 'user', text: 'What should we show in the MVP walkthrough?' },
    { id: 'demo-chat-02', ts: iso(211), role: 'orchestrator', text: 'Start with the cross-lane board, open DEMO-5 to connect execution with review evidence, then finish in project knowledge and token usage.' },
    { id: 'demo-chat-03', ts: iso(212), role: 'user', text: 'Keep the demo safe and repeatable.' },
    { id: 'demo-chat-04', ts: iso(213), role: 'orchestrator', text: 'Confirmed. This workspace contains seeded demo data only and can be reset before every capture.' },
  ]);
}

// ---- Wiki trees ---------------------------------------------------------

function projectDocsDir(root, projectKey) {
  return join(root, 'projects', projectKey, 'docs');
}

function renderWikiPage(page) {
  return `${[...page.lines, '', '---', '', PINNED_CONTENT.footer].join('\n')}\n`;
}

// The fingerprint shape is pinned to WikiCompanionStore.Fingerprint, which
// recomputes it on the running backend: a different byte count or line rule
// would make a freshly seeded page report itself as changed since review.
function writeWikiCompanion(docsDir, page, markdown) {
  const bytes = Buffer.from(markdown, 'utf8');
  writeJson(join(docsDir, `${page.relPath}.meta.json`), {
    $schema: 'https://agent-taskboard.local/schemas/wiki-document-companion.schema.json',
    schemaVersion: 'wiki-document-companion/v1',
    title: page.title,
    source: {
      path: `docs/${page.relPath}`,
      type: 'markdown',
      fingerprint: {
        algorithm: 'sha256',
        hash: createHash('sha256').update(bytes).digest('hex'),
        sizeBytes: bytes.length,
        lineCount: markdown.split('\n').length,
        capturedAt: iso(0),
      },
    },
    classification: {
      owner: page.classification.owner,
      documentMode: page.classification.documentMode,
      temporalState: page.classification.temporalState,
      implementationState: page.classification.implementationState,
      status: page.classification.status,
      type: page.classification.type,
      analyzedAt: iso(0).slice(0, 10),
    },
  });
}

function writeWikiTree(root, projectKey) {
  const tree = WIKI[projectKey];
  const docsDir = projectDocsDir(root, projectKey);
  for (const page of tree.pages) {
    const markdown = renderWikiPage(page);
    writeText(join(docsDir, page.relPath), markdown);
    writeWikiCompanion(docsDir, page, markdown);
  }
  // docs/app/ is a code-contract subtree the Wiki hides from every reading
  // surface, so the ordering and Overview configs never appear as pages.
  writeJson(join(docsDir, 'app', 'config', 'wiki-order.json'), {
    schemaVersion: 'wiki-order/v2',
    folderOrder: tree.order.folderOrder,
    fileOrder: tree.order.fileOrder,
  });
  writeJson(join(docsDir, 'app', 'config', 'home.json'), { sections: tree.home.sections });
}

// ---- Dossier gallery ----------------------------------------------------

// Read lazily and cached: a template that moved or changed its marker must fail
// the Dossier writer with a named cause, not kill task seeding at import time.
let articleStyle = null;
function articleStyleBlock() {
  if (articleStyle) return articleStyle;
  const match = readFileSync(ARTICLE_TEMPLATE_PATH, 'utf8')
    .match(/<style data-article-template="v2">[\s\S]*?<\/style>/);
  if (!match) {
    throw new Error(
      `Article template has no data-article-template="v2" style block: ${ARTICLE_TEMPLATE_PATH}`);
  }
  articleStyle = match[0];
  return articleStyle;
}

function escapeHtml(value) {
  // A missing field must fail the seed, not render the word "undefined" into a
  // Dossier that then passes every determinism and validity check.
  if (value === null || value === undefined) throw new Error('Dossier content has a missing field.');
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

// One escaping rule for the whole renderer: a field literally named `html`
// carries authored inline markup, every other string is escaped. Keeping that
// split visible in the field name is what stops seeded prose from silently
// becoming markup the public instance would render.
function renderBlock(block) {
  switch (block.kind) {
    case 'p':
      return `    <p>${block.html}</p>`;
    case 'dim':
      return `    <p class="dim">${block.html}</p>`;
    case 'h3':
      return `    <h3>${escapeHtml(block.text)}</h3>`;
    case 'list': {
      const tag = block.ordered ? 'ol' : 'ul';
      const items = block.items.map((item) => `      <li>${escapeHtml(item)}</li>`).join('\n');
      return `    <${tag}>\n${items}\n    </${tag}>`;
    }
    case 'table': {
      const head = block.head.map((cell) => `<th>${escapeHtml(cell)}</th>`).join('');
      const rows = block.rows
        .map((row) => `        <tr>${row.map((cell) => `<td>${escapeHtml(cell)}</td>`).join('')}</tr>`)
        .join('\n');
      return `    <table>\n      <thead><tr>${head}</tr></thead>\n      <tbody>\n${rows}\n      </tbody>\n    </table>`;
    }
    case 'callout':
      return `    <div class="callout" data-tone="${block.tone}"><h4>${escapeHtml(block.heading)}</h4><p>${block.html}</p></div>`;
    case 'variants': {
      const items = block.items
        .map((item) => `      <article class="variant"><h3>${escapeHtml(item.heading)}</h3><p>${item.html}</p></article>`)
        .join('\n');
      return `    <div class="variant-grid">\n${items}\n    </div>`;
    }
    case 'evidence': {
      const items = block.items
        .map((item) => `      <div class="evidence"><span class="evidence-class" data-evidence-class="${item.class}">${escapeHtml(item.class)}</span><span><b>${escapeHtml(item.title)}</b><small>${escapeHtml(item.note)}</small></span></div>`)
        .join('\n');
      return `    <div class="evidence-list">\n${items}\n    </div>`;
    }
    case 'decision': {
      const options = block.options
        .map((option) => `        <li data-option-id="${option.id}"><b>${escapeHtml(option.label)}</b> ${escapeHtml(option.note)}</li>`)
        .join('\n');
      return [
        `    <div class="box accent" data-decision-id="${block.id}" data-decision-kind="${block.decisionKind}">`,
        `      <h4>${escapeHtml(block.heading)}</h4>`,
        `      <p>${block.html}</p>`,
        '      <ul>',
        options,
        '      </ul>',
        '    </div>',
      ].join('\n');
    }
    default:
      throw new Error(`Unknown Dossier block kind: ${block.kind}`);
  }
}

function renderDossierHtml(item) {
  const doc = item.document;
  const meta = doc.meta
    .map(([label, value]) => `<span><b>${escapeHtml(label)}</b> ${escapeHtml(value)}</span>`)
    .join('');
  const sections = doc.sections
    .map((section) => [
      `  <section data-document-section="${section.section}">`,
      `    <h2><span class="number">${escapeHtml(section.number)}</span> ${escapeHtml(section.heading)}</h2>`,
      ...section.blocks.map(renderBlock),
      '  </section>',
    ].join('\n'))
    .join('\n\n');
  return `<!doctype html>
<html lang="en" data-document-pattern="concept">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${escapeHtml(item.title)} | Pinned demo Dossier</title>
  ${articleStyleBlock()}
</head>
<body>
<main class="article">
  <header class="page">
    <p class="kicker">${escapeHtml(doc.kicker)} · ${escapeHtml(item.key)}</p>
    <h1>${escapeHtml(item.title)}</h1>
    <p class="lede">${escapeHtml(doc.lede)}</p>
    <div class="meta">${meta}<span><b>Provenance</b> Pinned demo data</span></div>
  </header>

${sections}
</main>
</body>
</html>
`;
}

function buildDossierDescriptor(item) {
  const descriptor = {
    schemaVersion: 1,
    id: item.id,
    title: item.title,
    summary: item.summary,
    entrypoint: 'index.html',
    status: item.status,
    phase: item.phase,
    updatedAt: lifecycleIso(item.updatedAtMinutes),
    sourceTaskKeys: item.sourceTaskKeys,
    implementationTasks: [],
    relatedTaskKeys: item.relatedTaskKeys,
    key: item.key,
  };
  if (item.decision) {
    const source = item.decision;
    const decision = {
      outcome: source.outcome,
      state: source.state,
      operationId: source.operationId,
      preparedAt: lifecycleIso(source.preparedAtMinutes),
      preparedBy: source.preparedBy,
      confirmedAt: lifecycleIso(source.confirmedAtMinutes),
      confirmedBy: source.confirmedBy,
      decidedAt: lifecycleIso(source.decidedAtMinutes),
      spawnedTaskKeys: source.spawnedTaskKeys,
      responses: source.responses,
    };
    if (source.sourceRevision) decision.sourceRevision = source.sourceRevision;
    if (source.sourceFingerprint) decision.sourceFingerprint = source.sourceFingerprint;
    if (source.outcome === 'archive') decision.reason = source.reason;
    else decision.taskDraft = source.taskDraft;
    descriptor.decision = decision;
  }
  return descriptor;
}

/**
 * The two pinned inputs are joined by task and Dossier keys, and the snapshot
 * side is the one a sanitizing export regenerates. A refresh that adds or drops
 * a source card renumbers DEMO-*, which would silently reattach card-to-Dossier
 * edges to the wrong stories. Fail the seed instead, before a capture consumes
 * a store whose links no longer mean what the documents say.
 */
function validatePinnedContent() {
  const taskKeys = new Set(TASKS.map((task) => task.key));
  const dossierKeys = new Set(DOSSIERS.items.map((item) => item.key));
  const problems = [];

  for (const project of PROJECTS) {
    if (!WIKI[project.key]) problems.push(`No pinned Wiki content for project '${project.key}'.`);
  }
  if (!PROJECTS.some((project) => project.key === DOSSIERS.project)) {
    problems.push(`The Dossier gallery targets unknown project '${DOSSIERS.project}'.`);
  }
  for (const [taskKey, keys] of Object.entries(TASK_WORKBENCH_REFERENCES)) {
    if (!taskKeys.has(taskKey)) problems.push(`Reference map names unknown card '${taskKey}'.`);
    for (const key of keys) {
      if (!dossierKeys.has(key)) problems.push(`Card '${taskKey}' references unknown Dossier '${key}'.`);
    }
  }
  for (const item of DOSSIERS.items) {
    for (const key of [...item.sourceTaskKeys, ...item.relatedTaskKeys]) {
      if (!taskKeys.has(key)) problems.push(`Dossier '${item.key}' references unknown card '${key}'.`);
    }
  }

  if (problems.length > 0) {
    throw new Error(
      `Pinned content does not match the pinned snapshot:\n  ${problems.join('\n  ')}\n`
      + `Reconcile ${PINNED_CONTENT_PATH} with ${PINNED_SNAPSHOT_PATH}.`);
  }
}

function writeDossierGallery(root) {
  const galleryRoot = join(projectDocsDir(root, DOSSIERS.project), DOSSIERS.root);
  for (const item of DOSSIERS.items) {
    const dir = join(galleryRoot, item.id);
    writeJson(join(dir, 'workbench.json'), buildDossierDescriptor(item));
    writeText(join(dir, 'index.html'), renderDossierHtml(item));
  }
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

  validatePinnedContent();
  mkdirSync(root, { recursive: true });
  reset(root);

  TASKS.forEach((task, i) => writeTask(root, task, i));
  writeWorkspaceRootFiles(root);
  writePresentationStory(root);
  for (const project of PROJECTS) writeWikiTree(root, project.key);
  writeDossierGallery(root);
  writeDecisionJournal(root);
  stampTree(root);

  const perLane = TASKS.reduce((acc, t) => ((acc[t.state] = (acc[t.state] || 0) + 1), acc), {});
  const wikiPages = PROJECTS.reduce((sum, p) => sum + WIKI[p.key].pages.length, 0);
  console.log(`Seeded demo store at: ${root}`);
  console.log(`  projects: ${PROJECTS.map((p) => p.name).join(', ')}`);
  console.log(`  tasks:    ${TASKS.length} (${TASKS.filter((t) => t.history).length} with run/token history)`);
  console.log(`  pinned:   ${DECISION.taskKey} carries review, diff, screenshot, evidence, and decision state`);
  console.log(`  lanes:    ${Object.keys(perLane).sort().join(', ')}`);
  console.log(`  wiki:     ${wikiPages} pages across ${PROJECTS.length} projects`);
  console.log(`  dossiers: ${DOSSIERS.items.length} (${DOSSIERS.items.map((d) => `${d.key} ${d.status}`).join(', ')})`);
  console.log('Registry (.metadata) is left to the backend to seed from WatchPaths on first boot (ADR-0042).');
}

const __isMain = process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1];
if (__isMain) main();

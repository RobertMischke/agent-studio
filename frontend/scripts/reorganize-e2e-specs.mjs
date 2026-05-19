#!/usr/bin/env node
/**
 * Plan / dry-run / apply the e2e/ reorganisation.
 *
 *   node scripts/reorganize-e2e-specs.mjs          # print plan only
 *   node scripts/reorganize-e2e-specs.mjs --apply  # `git mv` each spec
 *
 * Heuristic: match the spec filename against ordered patterns. The
 * first match wins, so more specific patterns must come before
 * generic catch-alls. Anything that doesn't match shows up as
 * UNMATCHED so we can extend the table iteratively.
 */
import { readdirSync, existsSync, mkdirSync } from 'node:fs';
import { execSync } from 'node:child_process';

const ROOT = 'e2e';

// Ordered: first regex that matches the basename wins.
const RULES = [
  // ---- mocks + screenshot regressions ----
  [/-mockup(-screenshot)?(\.|$)/, 'mockups'],
  [/^meta-cycle-mockup/, 'mockups'],

  // ---- perf ----
  [/^perf-/, 'perf'],

  // ---- dev / stable / smoke ----
  [/^dev-(backend-fixture|icon-render|mode-banner)/, 'dev-tools'],
  [/^drive-stable$/, 'dev-tools'],
  [/^smoke-stable$/, 'dev-tools'],
  [/^mini-test$/, 'dev-tools'],
  [/^_refactor-baseline/, 'dev-tools'],

  // ---- chat surfaces (must be before any catch-all) ----
  [/^next-gen-chat-/, 'chat'],
  [/^chat-window-next-gen/, 'chat'],
  [/^activity-(chat|log|tab)-/, 'chat'],
  [/^chat-/, 'chat'],
  [/^workforce-chat/, 'chat'],
  [/^continuation-log-accumulation/, 'chat'],
  [/^token-bubble/, 'chat'],

  // ---- add-task / create-task flows ----
  [/^add-task/, 'add-task'],
  [/^create-job-with-screenshot/, 'add-task'],

  // ---- orchestrator (side sheet + sub-features) ----
  [/^orchestrator-/, 'orchestrator'],

  // ---- project-chat lives under orchestrator's umbrella (Slice D) ----
  [/^project-chat-/, 'orchestrator'],

  // ---- visual evidence (screenshots, lightbox, reel) ----
  [/^visual-evidence/, 'visual-evidence'],
  [/screenshots?\.spec\.ts$/, 'visual-evidence'],
  [/^job-screenshots-in-protocol/, 'visual-evidence'],
  [/^prompt-screenshot/, 'visual-evidence'],
  [/^task-description-image-lightbox/, 'visual-evidence'],
  [/^readme-screenshots/, 'visual-evidence'],
  [/^session-chip-screenshot/, 'visual-evidence'],
  [/^unified-dialog-screenshots/, 'visual-evidence'],

  // ---- cli / claude / codex / gemini ----
  [/^claude-/, 'cli'],
  [/^gemini-/, 'cli'],
  [/^cli-/, 'cli'],
  [/^quota$/, 'cli'],

  // ---- git ----
  [/^git-/, 'git'],
  [/^commit-tooltip-overflow/, 'git'],

  // ---- studio-shell / layout / vscode ----
  [/^vscode-layout-/, 'layout'],
  [/^status-bar-/, 'layout'],
  [/^header-(buttons-cleanup|filter)/, 'layout'],
  [/^drag-auto-scroll/, 'layout'],
  [/^kanban-filter-sidesheet/, 'layout'],

  // ---- task / job detail ----
  [/^detail-/, 'task-detail'],
  [/^task-detail-/, 'task-detail'],
  [/^prompt-(edit-lock|save)/, 'task-detail'],
  [/^inspector-tab-default/, 'task-detail'],
  [/^log-overlay-centering/, 'task-detail'],
  [/^triage-panel/, 'task-detail'],
  [/^delete-task/, 'task-detail'],
  [/^open-failed-task/, 'task-detail'],
  [/^repository-hygiene-strip/, 'task-detail'],
  [/^review-evidence-panel/, 'task-detail'],
  [/^session-task-link-chip/, 'task-detail'],
  [/^session-events/, 'task-detail'],
  [/^protocol-/, 'task-detail'],
  [/^runtime-console-capture/, 'task-detail'],
  [/^verbose-debug-overlay/, 'task-detail'],
  [/^job-results-html-render/, 'task-detail'],

  // ---- project-detail / drift / observability ----
  [/^project-(drift|observability|product-runtime|security|uxui|token-usage|tab-tokens|shell-rail|analysis|docs|steering|identity)/, 'project'],
  [/^workspace-token-timeline/, 'project'],

  // ---- board / kanban / lanes / dnd ----
  [/^kanban-/, 'board'],
  [/^lane-/, 'board'],
  [/^backlog-lane-and-tags/, 'board'],
  [/^archive-/, 'board'],
  [/^auto-(pickup|review)-/, 'board'],
  [/^bug-cross-project-counter/, 'board'],
  [/^board-/, 'board'],
  [/^card-live-state-by-lane/, 'board'],
  [/^client-attribution/, 'board'],
  [/^compact-cards-toggle/, 'board'],
  [/^cross-lane-drop-position/, 'board'],
  [/^dnd-no-flash/, 'board'],
  [/^optimistic-reorder-evidence/, 'board'],
  [/^info-button-lane-headers/, 'board'],
  [/^failed-pickup-lane/, 'board'],

  // ---- cross-cutting system ----
  [/^caret-suppression/, 'system'],
  [/^concept-help/, 'system'],
  [/^escape-modal-stack/, 'system'],
  [/^live-decision-banner/, 'system'],
  [/^markdown-body-consolidation/, 'system'],
  [/^stop-no-error-modal/, 'system'],
  [/^update-/, 'system'],
];

const files = readdirSync(ROOT, { withFileTypes: true })
  .filter(d => d.isFile() && d.name.endsWith('.spec.ts'))
  .map(d => d.name);

const plan = new Map(); // folder → string[]
const unmatched = [];

for (const f of files) {
  let matched = false;
  for (const [re, folder] of RULES) {
    if (re.test(f.replace(/\.spec\.ts$/, ''))) {
      if (!plan.has(folder)) plan.set(folder, []);
      plan.get(folder).push(f);
      matched = true;
      break;
    }
  }
  if (!matched) unmatched.push(f);
}

const sortedFolders = [...plan.keys()].sort();
console.log(`Plan: ${files.length} specs, ${sortedFolders.length} folders, ${unmatched.length} unmatched.\n`);
for (const folder of sortedFolders) {
  const items = plan.get(folder).sort();
  console.log(`${folder}/  (${items.length})`);
  for (const f of items) console.log(`    ${f}`);
  console.log('');
}
if (unmatched.length > 0) {
  console.log('UNMATCHED:');
  for (const f of unmatched) console.log(`    ${f}`);
}

const apply = process.argv.includes('--apply');
if (!apply) {
  console.log('\nDry run — pass --apply to git mv.');
  process.exit(0);
}

if (unmatched.length > 0) {
  console.error('\nRefusing to apply with unmatched specs; extend RULES and re-run.');
  process.exit(1);
}

for (const folder of sortedFolders) {
  const target = `${ROOT}/${folder}`;
  if (!existsSync(target)) mkdirSync(target, { recursive: true });
  for (const f of plan.get(folder)) {
    const from = `${ROOT}/${f}`;
    const to = `${target}/${f}`;
    execSync(`git mv "${from}" "${to}"`, { stdio: 'inherit' });
  }
}
console.log('\nDone.');

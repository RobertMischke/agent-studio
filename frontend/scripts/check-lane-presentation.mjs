#!/usr/bin/env node
/**
 * Keep canonical lane names in models/lane-presentation.ts. The small allow
 * list below covers words that are also used outside the lane domain. Counts
 * are capped so adding another occurrence in an allowed file still fails.
 */
import { existsSync, readFileSync } from 'node:fs';
import { execSync } from 'node:child_process';

const presentationFile = 'src/app/models/lane-presentation.ts';
const presentationSource = readFileSync(presentationFile, 'utf8');
const laneNames = new Set(
  [...presentationSource.matchAll(/\b(?:displayName|shortName):\s*'([^']+)'/g)].map(match => match[1]),
);

const allowedNonLaneUses = new Map([
  ['src/app/features/task-server/components/task-server-panel/task-server-panel.html\0Ready', 1],
  ['src/app/features/project-detail/components/project-cli-environment-section/project-cli-environment-section.ts\0Ready', 1],
  ['src/app/features/project-detail/components/project-url-preview-tab/project-url-preview-tab.component.html\0Ready', 1],
  ['src/app/features/project-detail/components/project-deployment-panel/project-deployment-panel.component.ts\0Ready', 1],
  ['src/app/features/project-detail/components/project-deployment-panel/project-deployment-panel.component.html\0Delivered', 1],
  ['src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.html\0Delivered', 1],
  ['src/app/features/shell/components/workspace-management/workspace-management.component.ts\0Archive', 1],
  ['src/app/features/shell/components/workspace-management/workspace-management.component.html\0Archive', 1],
  ['src/app/features/project-detail/components/page-action-bar/page-action-bar.html\0Archive', 1],
  ['src/app/features/project-detail/components/workbench-decision-panel/workbench-decision-panel.html\0Archive', 1],
]);

const files = execSync('git ls-files --cached --others --exclude-standard src/app', { encoding: 'utf8' })
  .split('\n')
  .filter(Boolean)
  .filter(existsSync)
  .filter(file => (file.endsWith('.ts') || file.endsWith('.html')) && !file.endsWith('.spec.ts'))
  .filter(file => file !== presentationFile);

const violations = [];
for (const file of files) {
  const source = stripComments(readFileSync(file, 'utf8'), file.endsWith('.html'));
  const occurrences = new Map();

  for (const match of source.matchAll(/(['"`])([^'"`\r\n]*)\1/g)) {
    if (laneNames.has(match[2])) record(match[2], match.index ?? 0);
  }
  if (file.endsWith('.html')) {
    for (const match of source.matchAll(/>\s*([^<>{}\r\n]+?)\s*</g)) {
      const value = match[1].trim();
      if (laneNames.has(value)) record(value, (match.index ?? 0) + match[0].indexOf(value));
    }
  }

  function record(name, offset) {
    const key = `${file}\0${name}`;
    const entries = occurrences.get(key) ?? [];
    entries.push(lineAt(source, offset));
    occurrences.set(key, entries);
  }

  for (const [key, lines] of occurrences) {
    const allowed = allowedNonLaneUses.get(key) ?? 0;
    if (lines.length <= allowed) continue;
    const name = key.slice(key.indexOf('\0') + 1);
    for (const line of lines.slice(allowed)) violations.push({ file, line, name });
  }
}

if (violations.length > 0) {
  console.error(`\nFound ${violations.length} hard-coded lane presentation literal(s):\n`);
  for (const violation of violations) {
    console.error(`  - ${violation.file}:${violation.line} \"${violation.name}\"`);
  }
  console.error(`\nRead lane names from ${presentationFile} via lanePresentation(), laneDisplayName(), or laneShortName().\n`);
  process.exit(1);
}

console.log(`OK: ${laneNames.size} lane names are centralized in ${presentationFile}.`);

function stripComments(source, html) {
  let result = source.replace(/\/\*[\s\S]*?\*\//g, match => match.replace(/[^\r\n]/g, ' '));
  if (html) result = result.replace(/<!--[\s\S]*?-->/g, match => match.replace(/[^\r\n]/g, ' '));
  else result = result.replace(/(^|[^:])\/\/.*$/gm, match => match.replace(/[^\r\n]/g, ' '));
  return result;
}

function lineAt(source, offset) {
  return source.slice(0, offset).split('\n').length;
}

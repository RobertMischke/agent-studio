#!/usr/bin/env node
/**
 * Prevent user-facing workflow-lane names from acquiring a second source.
 *
 * Names are read from LanePresentation itself. A matching literal is rejected
 * everywhere in production source. Marker-based exceptions cover the few UI
 * actions and health indicators whose ordinary-language labels happen to be
 * lane-name homonyms. Tests and the canonical module may assert exact copy.
 */
import { existsSync, readFileSync } from 'node:fs';
import { execSync } from 'node:child_process';

const root = 'src/app';
const presentationFile = `${root}/models/lane-presentation.model.ts`;
const presentationSource = readFileSync(presentationFile, 'utf8');
const names = new Set();
const laneEntry = /lane\(TaskState\.\w+,\s*'([^']+)',\s*'([^']+)'/g;
for (const match of presentationSource.matchAll(laneEntry)) {
  names.add(match[1]);
  names.add(match[2]);
}

const nonLaneHomonyms = [
  ['src/app/features/project-detail/components/project-cli-environment-section/project-cli-environment-section.ts', 'Ready', 'row.available'],
  ['src/app/features/task-server/components/task-server-panel/task-server-panel.html', 'Ready', '<dt>Readiness</dt>'],
  ['src/app/features/project-detail/components/project-url-preview-tab/project-url-preview-tab.component.html', 'Ready', 'detail.iframeReady'],
  ['src/app/features/project-detail/components/project-deployment-panel/project-deployment-panel.component.ts', 'Ready', 'target.runnable'],
  ['src/app/features/shell/components/workspace-management/workspace-management.component.ts', 'Archive', 'const verb = archived'],
  ['src/app/features/shell/components/workspace-management/workspace-management.component.html', 'Archive', 'p.archived'],
  ['src/app/features/project-detail/components/page-action-bar/page-action-bar.html', 'Archive', 'archived()'],
  ['src/app/features/project-detail/components/workbench-decision-panel/workbench-decision-panel.html', 'Archive', 'beginArchive()'],
  ['src/app/features/project-detail/components/project-deployment-panel/project-deployment-panel.component.html', 'Delivered', 'run.jobsSinceLastRestart'],
  ['src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.html', 'Delivered', '<span>Delivered</span>'],
];

const files = execSync(`git ls-files --cached --others --exclude-standard ${root}`, { encoding: 'utf8' })
  .split('\n')
  .filter(existsSync)
  .filter(file => /\.(ts|html)$/.test(file))
  .filter(file => file !== presentationFile)
  .filter(file => !file.endsWith('.spec.ts'));

const errors = [];
for (const file of files) {
  const source = readFileSync(file, 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, block => block.replace(/[^\n]/g, ' '))
    .replace(/\/\/.*$/gm, '');
  const lines = source.split(/\r?\n/);
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    for (const name of names) {
      const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      const quoted = new RegExp(`(['\"])${escaped}\\1`);
      const textNode = new RegExp(`>\\s*${escaped}\\s*<`);
      if (!quoted.test(line) && !textNode.test(line)) continue;
      if (nonLaneHomonyms.some(([allowedFile, allowedName, marker]) =>
        file === allowedFile && name === allowedName && line.includes(marker))) continue;
      errors.push({ file, line: index + 1, name });
    }
  }
}

if (errors.length > 0) {
  console.error(`\nFound ${errors.length} hard-coded lane presentation literal(s):\n`);
  for (const error of errors) {
    console.error(`  - ${error.file}:${error.line} '${error.name}'`);
  }
  console.error(`\nRead names from ${presentationFile} via lanePresentation(), laneName(), or laneShortName().\n`);
  process.exit(1);
}

console.log(`OK: scanned ${files.length} production files; lane names come from LanePresentation.`);

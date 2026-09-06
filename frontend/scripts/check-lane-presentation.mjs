#!/usr/bin/env node
/**
 * Prevent production UI code from creating a second source for canonical lane
 * names. Names are read from LanePresentation itself, so this guard does not
 * carry a shadow copy of the vocabulary it enforces.
 */
import { existsSync, readFileSync } from 'node:fs';
import { execSync } from 'node:child_process';
import ts from 'typescript';

const presentationFile = 'src/app/models/lane-presentation.ts';
const presentationSource = ts.createSourceFile(
  presentationFile,
  readFileSync(presentationFile, 'utf8'),
  ts.ScriptTarget.Latest,
  true,
);
const laneNames = new Set();

function collectPresentationNames(node) {
  if (
    ts.isPropertyAssignment(node)
    && ts.isIdentifier(node.name)
    && (node.name.text === 'displayName' || node.name.text === 'shortName')
    && ts.isStringLiteralLike(node.initializer)
  ) {
    laneNames.add(node.initializer.text);
  }
  ts.forEachChild(node, collectPresentationNames);
}
collectPresentationNames(presentationSource);

if (laneNames.size === 0) {
  console.error(`Could not read lane names from ${presentationFile}.`);
  process.exit(1);
}

// These strings describe non-lane concepts. Counts keep the exceptions
// bounded: another occurrence in the same file still fails.
const allowedNonLaneLiteralCounts = new Map([
  ['src/app/features/project-detail/components/project-cli-environment-section/project-cli-environment-section.ts\0Ready', 1],
  ['src/app/features/project-detail/components/project-deployment-panel/project-deployment-panel.component.ts\0Ready', 1],
  ['src/app/features/shell/components/workspace-management/workspace-management.component.ts\0Archive', 1],
  ['src/app/features/project-detail/components/workbench-decision-panel/workbench-decision-panel.html\0Archive', 1],
  ['src/app/features/project-detail/components/project-deployment-panel/project-deployment-panel.component.html\0Delivered', 1],
  ['src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.html\0Delivered', 1],
]);

const files = execSync('git ls-files --cached --others --exclude-standard src/app', { encoding: 'utf8' })
  .split('\n')
  .filter(existsSync)
  .filter((file) => (file.endsWith('.ts') || file.endsWith('.html')))
  .filter((file) => !file.endsWith('.spec.ts') && file !== presentationFile);

const occurrences = [];
for (const file of files) {
  const text = readFileSync(file, 'utf8');
  if (file.endsWith('.ts')) {
    const source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
    const visit = (node) => {
      if (ts.isStringLiteralLike(node) && laneNames.has(node.text)) {
        const point = source.getLineAndCharacterOfPosition(node.getStart(source));
        occurrences.push({ file, name: node.text, line: point.line + 1 });
      }
      ts.forEachChild(node, visit);
    };
    visit(source);
    continue;
  }

  for (const name of laneNames) {
    const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const exactUiText = new RegExp(`>\\s*${escaped}\\s*<|=(["'])${escaped}\\1`, 'g');
    for (const match of text.matchAll(exactUiText)) {
      const line = text.slice(0, match.index).split('\n').length;
      occurrences.push({ file, name, line });
    }
  }
}

const counts = new Map();
const violations = [];
for (const occurrence of occurrences) {
  const key = `${occurrence.file}\0${occurrence.name}`;
  const count = (counts.get(key) ?? 0) + 1;
  counts.set(key, count);
  if (count > (allowedNonLaneLiteralCounts.get(key) ?? 0)) violations.push(occurrence);
}

if (violations.length > 0) {
  console.error('\nFound hard-coded canonical lane name(s) outside LanePresentation:\n');
  for (const violation of violations) {
    console.error(`  - ${violation.file}:${violation.line} ${JSON.stringify(violation.name)}`);
  }
  console.error(`\nRead lane names through ${presentationFile} instead.\n`);
  process.exit(1);
}

console.log(`OK: ${laneNames.size} canonical lane names are owned by LanePresentation.`);

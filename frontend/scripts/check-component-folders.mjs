#!/usr/bin/env node
/**
 * Enforce folder-per-component layout under `src/app/`.
 *
 * Hard rule: a folder must contain at most one Angular component
 * (`.ts` file declaring `@Component`). Each component lives in its own
 * folder alongside its `.html` / `.scss` files.
 *
 * Hard rule: the containing folder's basename should match the
 * component file's basename (minus the optional `.component` suffix).
 * Existing descriptor-style names stay as explicit baseline exceptions;
 * new mismatches fail the lint gate.
 *
 * Examples that PASS:
 *   src/app/components/tooltip/tooltip.directive.ts          (utility, no @Component)
 *   src/app/features/cli/components/cli-console/cli-console.ts
 *   src/app/features/cli/components/cli-console/cli-console.component.ts
 *
 * Example that FAILS:
 *   src/app/features/cli/components/multi/a.component.ts
 *   src/app/features/cli/components/multi/b.component.ts     (two components in one folder)
 */
import { existsSync, readFileSync } from 'node:fs';
import { basename, dirname } from 'node:path';
import { execSync } from 'node:child_process';

const root = 'src/app';
const sourcePaths = execSync(`git ls-files --cached --others --exclude-standard ${root}`, { encoding: 'utf8' })
  .split('\n')
  .filter(existsSync);
const files = sourcePaths
  .filter(f => f.endsWith('.ts') && !f.endsWith('.spec.ts') && !f.endsWith('.d.ts'));

const errors = [];
const warnings = [];
const byDir = new Map();
const allowedFolderNameMismatches = new Set([
  'src/app/components/aspect-findings/aspect-findings-list.component.ts',
  'src/app/features/project-detail/components/project-observability/project-observability-panel.component.ts',
  'src/app/features/project-detail/components/project-product-runtime/project-product-runtime-panel.component.ts',
]);

for (const file of files) {
  const text = readFileSync(file, 'utf8');
  if (!/^\s*@Component\s*\(/m.test(text)) continue;

  const compName = basename(file).replace(/\.ts$/, '').replace(/\.component$/, '');
  const dir = dirname(file);
  const dirName = basename(dir);

  if (dirName !== compName && allowedFolderNameMismatches.has(file)) {
    warnings.push({
      file,
      reason: `folder '${dirName}/' does not match component name '${compName}' (baseline exception)`,
    });
  } else if (dirName !== compName) {
    errors.push({
      file,
      reason: `folder '${dirName}/' does not match component name '${compName}'`,
    });
  }

  if (!byDir.has(dir)) byDir.set(dir, []);
  byDir.get(dir).push({ file, compName });
}

for (const [dir, entries] of byDir) {
  if (entries.length > 1) {
    errors.push({
      file: dir,
      reason: `folder contains ${entries.length} component files: ${entries.map(e => e.compName).join(', ')} — split each into its own subfolder`,
    });
  }
}

// Lane presentation is a structural boundary, not a component convention.
// Read the canonical names from the map itself so this lint gate cannot drift
// when product wording changes. Files that work with TaskState may not author
// those names again. Distinctive multi-word lane names are protected globally,
// which also catches a component that uses a raw transport key instead of
// importing TaskState.
const lanePresentationFile = `${root}/models/lane-presentation.ts`;
const lanePresentationSource = readFileSync(lanePresentationFile, 'utf8');
const laneNames = [...new Set(
  [...lanePresentationSource.matchAll(/(?:displayName|shortName): '([^']+)'/g)].map(match => match[1]),
)];
const globallyReservedLaneNames = new Set([
  'Human review',
  'Post Processing',
  'Orchestrator Prep',
  'Failed pickup',
  'Code not complete',
]);

for (const file of sourcePaths) {
  if (file === lanePresentationFile || file.endsWith('.spec.ts') || !/\.(?:ts|html)$/.test(file)) continue;
  const source = readFileSync(file, 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/\/\/.*$/gm, '')
    .replace(/<!--[\s\S]*?-->/g, '');
  const usesTaskState = source.includes('TaskState.');

  for (const name of laneNames) {
    if (!usesTaskState && !globallyReservedLaneNames.has(name)) continue;
    const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const literal = new RegExp("(['\"`])" + escaped + '\\1', 'g');
    for (const match of source.matchAll(literal)) {
      const line = source.slice(0, match.index).split('\n').length;
      errors.push({
        file: `${file}:${line}`,
        reason: `hard-coded lane name '${name}'; read it from LanePresentation`,
      });
    }
  }
}

if (warnings.length > 0) {
  console.warn(`\n${warnings.length} folder-name baseline exception(s) (non-blocking):\n`);
  for (const w of warnings) {
    console.warn(`  - ${w.file}`);
    console.warn(`    ${w.reason}`);
  }
}

if (errors.length > 0) {
  console.error(`\nFound ${errors.length} component-folder violation(s):\n`);
  for (const v of errors) {
    console.error(`  - ${v.file}`);
    console.error(`    ${v.reason}`);
  }
  console.error('\nRules: each Angular component owns a matching folder, and lane presentation');
  console.error('strings live only in src/app/models/lane-presentation.ts.');
  console.error('See AGENTS.md (Frontend / folder-per-component) for details.\n');
  process.exit(1);
}

console.log(`OK: scanned ${files.length} .ts files; no two-components-per-folder violations.`);
if (warnings.length > 0) {
  console.log(`(${warnings.length} folder-name baseline exception(s) above — remove after renaming.)`);
}

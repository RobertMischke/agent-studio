#!/usr/bin/env node
/**
 * Reject authored lane names outside the LanePresentation catalogue.
 *
 * The guarded names are read from the catalogue itself, so this check cannot
 * drift when product wording changes. Tests may quote expected copy; product
 * TypeScript must resolve lane copy through lane-presentation.ts.
 */
import { existsSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';

const catalogue = 'src/app/models/lane-presentation.ts';
const catalogueText = readFileSync(catalogue, 'utf8');
const guardedNames = new Set();
for (const match of catalogueText.matchAll(/\b(?:name|shortName):\s*(['"])(.*?)\1/g)) {
  guardedNames.add(match[2]);
}

const files = execFileSync(
  'git',
  ['ls-files', '--cached', '--others', '--exclude-standard', 'src/app'],
  { encoding: 'utf8' },
)
  .split('\n')
  .filter(Boolean)
  .filter(existsSync)
  .filter((file) => file.endsWith('.ts'))
  .filter((file) => !file.endsWith('.spec.ts'))
  .filter((file) => file !== catalogue);

const violations = [];
for (const file of files) {
  const source = readFileSync(file, 'utf8');
  for (const literal of stringLiterals(source)) {
    const sourceLine = source.split(/\r?\n/, literal.line)[literal.line - 1] ?? '';
    if (sourceLine.includes('lane-presentation-lint: allow')) continue;
    if (guardedNames.has(literal.value)) violations.push({ file, ...literal });
  }
}

if (violations.length > 0) {
  console.error(`\nFound ${violations.length} hard-coded lane presentation literal(s):\n`);
  for (const violation of violations) {
    console.error(`  - ${violation.file}:${violation.line}  ${JSON.stringify(violation.value)}`);
  }
  console.error('\nResolve lane copy through src/app/models/lane-presentation.ts.');
  console.error('Use // lane-presentation-lint: allow only when the text is demonstrably not a lane name.\n');
  process.exit(1);
}

console.log(`OK: scanned ${files.length} product TypeScript files; lane presentation literals are centralised.`);

/** Minimal string scanner that deliberately ignores line and block comments. */
function stringLiterals(source) {
  const literals = [];
  let index = 0;
  let line = 1;
  while (index < source.length) {
    const char = source[index];
    const next = source[index + 1];
    if (char === '\n') {
      line++;
      index++;
      continue;
    }
    if (char === '/' && next === '/') {
      index = source.indexOf('\n', index + 2);
      if (index < 0) break;
      continue;
    }
    if (char === '/' && next === '*') {
      const end = source.indexOf('*/', index + 2);
      const block = source.slice(index, end < 0 ? source.length : end + 2);
      line += (block.match(/\n/g) ?? []).length;
      index = end < 0 ? source.length : end + 2;
      continue;
    }
    if (char !== "'" && char !== '"' && char !== '`') {
      index++;
      continue;
    }
    const quote = char;
    const startLine = line;
    let value = '';
    index++;
    while (index < source.length) {
      const current = source[index];
      if (current === '\\') {
        value += current + (source[index + 1] ?? '');
        index += 2;
        continue;
      }
      if (current === quote) {
        index++;
        break;
      }
      if (current === '\n') line++;
      value += current;
      index++;
    }
    literals.push({ line: startLine, value });
  }
  return literals;
}

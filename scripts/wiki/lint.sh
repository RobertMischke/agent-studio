#!/usr/bin/env bash
# Validate every docs/common-problems/<slug>/ entry.
# Exits non-zero with a per-file reason on failure. Silent on success except for a final OK line.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"

node - "$repo_root" <<'NODE'
const fs = require('fs');
const path = require('path');

const repoRoot = process.argv[2];
const root = path.join(repoRoot, 'docs', 'wiki', 'common-problems');

const requiredKeys = ['id', 'title', 'status', 'first-seen', 'last-seen', 'severity', 'category', 'tags', 'affects', 'related-tasks', 'related-adrs'];
const listKeys = new Set(['tags', 'affects', 'related-tasks', 'related-adrs']);
const allowedStatus = new Set(['open', 'mitigated', 'fixed', 'archived']);
const allowedSeverity = new Set(['blocker', 'major', 'minor', 'nuisance']);
const allowedCategory = new Set(['permission', 'filesystem', 'cli', 'runner', 'ui', 'state-machine', 'misc']);
const scaffoldPlaceholderPattern = /TODO: one-line human-readable title|TODO: one-sentence symptom description|TODO: best current understanding|TODO: shortest reliable mitigation|TODO: the fix or design change|TODO: detailed analyses, reproducers, log excerpts|Hypotheses, open questions, ruled-out approaches\. Move into measures\.md once attempted\./;
const todoTableCellPattern = /\|\s*TODO\s*\|/;

let errors = 0;
let checked = 0;

function fail(rel, message) {
  console.error(`lint: ${rel}: ${message}`);
  errors += 1;
}

function parseFrontmatter(text) {
  const lines = text.split(/\r?\n/);
  if (lines[0]?.trim() !== '---') {
    return null;
  }

  const values = new Map();
  for (let i = 1; i < lines.length; i += 1) {
    const line = lines[i];
    if (line.trim() === '---') {
      return values;
    }

    const match = /^([A-Za-z0-9-]+):\s*(.*)$/.exec(line);
    if (match) {
      values.set(match[1], match[2].trim());
    }
  }

  return null;
}

function readIfExists(file) {
  if (!fs.existsSync(file)) {
    return null;
  }

  return fs.readFileSync(file, 'utf8');
}

for (const dirent of fs.readdirSync(root, { withFileTypes: true })) {
  if (!dirent.isDirectory() || dirent.name === 'archive') {
    continue;
  }

  const slug = dirent.name;
  const dir = path.join(root, slug);
  const readme = path.join(dir, 'README.md');
  const rel = `docs/common-problems/${slug}/README.md`;
  checked += 1;

  const readmeText = readIfExists(readme);
  if (readmeText === null) {
    fail(rel, 'missing README.md');
    continue;
  }

  const frontmatter = parseFrontmatter(readmeText);
  if (frontmatter === null) {
    fail(rel, 'missing or malformed YAML frontmatter block');
    continue;
  }

  if (scaffoldPlaceholderPattern.test(readmeText)) {
    fail(rel, 'README.md still contains scaffold placeholder text');
  }

  for (const key of requiredKeys) {
    if (!frontmatter.has(key)) {
      fail(rel, `missing required key: ${key}`);
      continue;
    }

    if (!listKeys.has(key) && frontmatter.get(key) === '') {
      fail(rel, `missing required key: ${key}`);
    }
  }

  const id = frontmatter.get('id');
  if (id && id !== slug) {
    fail(rel, `id (${id}) does not match folder name (${slug})`);
  }

  const status = frontmatter.get('status');
  if (status && !allowedStatus.has(status)) {
    fail(rel, `status not in [${Array.from(allowedStatus).join(' ')}]: ${status}`);
  }

  const severity = frontmatter.get('severity');
  if (severity && !allowedSeverity.has(severity)) {
    fail(rel, `severity not in [${Array.from(allowedSeverity).join(' ')}]: ${severity}`);
  }

  const category = frontmatter.get('category');
  if (category && !allowedCategory.has(category)) {
    fail(rel, `category not in [${Array.from(allowedCategory).join(' ')}]: ${category}`);
  }

  for (const sibling of ['occurrences.md', 'protocol.md', 'measures.md', 'ideas.md', 'related.md']) {
    if (!fs.existsSync(path.join(dir, sibling))) {
      fail(rel, `sibling file missing: ${sibling}`);
    }
  }

  const occurrences = readIfExists(path.join(dir, 'occurrences.md'));
  if (occurrences !== null && todoTableCellPattern.test(occurrences)) {
    fail(rel, 'occurrences.md still contains scaffold TODO row');
  }

  const measures = readIfExists(path.join(dir, 'measures.md'));
  if (measures !== null) {
    if (scaffoldPlaceholderPattern.test(measures)) {
      fail(rel, 'measures.md still contains scaffold placeholder text');
    }
    if (todoTableCellPattern.test(measures)) {
      fail(rel, 'measures.md still contains scaffold TODO row');
    }
  }
}

if (errors > 0) {
  console.error(`lint: ${errors} error(s) across ${checked} folder(s)`);
  process.exit(1);
}

console.log(`lint: ok (${checked} folder(s))`);
NODE

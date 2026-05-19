#!/usr/bin/env node
/**
 * Per-component health audit. For every Angular component under
 * src/app/ (each `.ts` file declaring `@Component`), collect the
 * machine-detectable issues that often hide tech debt:
 *
 *   size          — LOC of .ts + .html + .scss; flags very large files.
 *   orphan-scss   — selectors in .scss never referenced in template/ts;
 *                   indicates dead code from a half-finished migration.
 *   hex-colour    — `#abcdef` in .scss outside `:root` / `:host`; should
 *                   be a `var(--studio-…)` token to follow the theme.
 *   inline-tpl    — `template:` or `styles:` metadata; AGENTS.md forbids.
 *   deep-import   — cross-feature imports of `features/X/components/...`
 *                   instead of the feature barrel (`'./features/X'`).
 *   legacy-decor  — `@Input` / `@Output` instead of `input()` / `output()`.
 *   no-spec       — no sibling .spec.ts.
 *   mouse-evt     — handler typed `MouseEvent` (re-broken by Angular's
 *                   strict template check once a `(keydown.enter)` lands).
 *
 * Output is a JSON map per component. With `--md` it renders a
 * markdown report grouped by severity instead.
 */
import { readFileSync, statSync, existsSync } from 'node:fs';
import { dirname, basename, join } from 'node:path';
import { execSync } from 'node:child_process';

const ROOT = 'src/app';
const files = execSync(`git ls-files ${ROOT}`, { encoding: 'utf8' })
  .split('\n').filter(Boolean);

const tsFiles = files.filter(f => f.endsWith('.ts') && !f.endsWith('.spec.ts') && !f.endsWith('.d.ts'));
const htmlFiles = files.filter(f => f.endsWith('.html'));
const allTextCache = new Map();
function readAll(f) {
  if (!allTextCache.has(f)) {
    try { allTextCache.set(f, readFileSync(f, 'utf8')); } catch { allTextCache.set(f, ''); }
  }
  return allTextCache.get(f);
}

function loc(p) {
  try { return readFileSync(p, 'utf8').split(/\r?\n/).length; } catch { return 0; }
}

// Build a global "is this class name referenced anywhere outside its
// defining scss" check (same idea as find-orphan-css-classes.mjs).
const haystack = [...htmlFiles, ...tsFiles].map(readAll).join('\n');
function usedAnywhere(name) {
  if (!haystack.includes(name)) return false;
  const re = new RegExp(`(^|[^A-Za-z0-9_-])${escapeRe(name)}([^A-Za-z0-9_-]|$)`);
  for (const f of [...htmlFiles, ...tsFiles]) {
    if (re.test(readAll(f))) return true;
  }
  return false;
}
function escapeRe(s) { return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }

function extractClasses(scss) {
  const out = new Set();
  const clean = scss
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/(^|[^:])\/\/.*$/gm, '$1');
  const re = /(^|[^A-Za-z0-9_-])\.([A-Za-z][A-Za-z0-9_-]+)/g;
  let m;
  while ((m = re.exec(clean)) !== null) {
    if (!m[2].startsWith('ng-')) out.add(m[2]);
  }
  return out;
}

const reports = [];

for (const ts of tsFiles) {
  const text = readAll(ts);
  if (!/^\s*@Component\s*\(/m.test(text)) continue;

  const dir = dirname(ts);
  const base = basename(ts).replace(/\.ts$/, '').replace(/\.component$/, '');

  // Sibling files
  const html = files.find(f => f === join(dir, basename(ts).replace('.ts', '.html')))
    || files.find(f => f.startsWith(dir + '/') && f.endsWith('.html'));
  const scss = files.find(f => f === join(dir, basename(ts).replace('.ts', '.scss')))
    || files.find(f => f.startsWith(dir + '/') && f.endsWith('.scss') && !f.endsWith('.spec.ts'));
  const spec = files.find(f => f === ts.replace(/\.ts$/, '.spec.ts'));

  const tsLoc = loc(ts);
  const htmlLoc = html ? loc(html) : 0;
  const scssLoc = scss ? loc(scss) : 0;
  const total = tsLoc + htmlLoc + scssLoc;

  const issues = [];

  // size
  if (total > 800) issues.push({ kind: 'size', detail: `${total} LOC (.ts ${tsLoc} + .html ${htmlLoc} + .scss ${scssLoc})` });

  // orphan-scss
  if (scss) {
    const declared = extractClasses(readAll(scss));
    const orphans = [...declared].filter(c => !usedAnywhere(c));
    if (orphans.length > 0) issues.push({ kind: 'orphan-scss', detail: `${orphans.length} class(es): ${orphans.slice(0, 5).join(', ')}${orphans.length > 5 ? '…' : ''}` });
  }

  // hex-colour outside :root/:host
  if (scss) {
    const scssText = readAll(scss);
    // Strip :root, :host, and `var(--token, #fallback)` fallbacks — the
    // hex inside a var() fallback IS the token system, not bypassing it.
    const stripped = scssText
      .replace(/:host\b[^{]*\{[\s\S]*?\}/g, '')
      .replace(/:root\b[^{]*\{[\s\S]*?\}/g, '')
      .replace(/var\(\s*--[A-Za-z0-9_-]+\s*,\s*#[0-9a-fA-F]{3,8}\b[^)]*\)/g, '');
    const matches = stripped.match(/#[0-9a-fA-F]{3,8}\b/g) ?? [];
    if (matches.length > 0) issues.push({ kind: 'hex-colour', detail: `${matches.length} hard-coded hex` });
  }

  // inline-tpl
  if (/(\btemplate\s*:|\bstyles\s*:|\bstyleUrls\s*:\s*\[)/.test(text)) {
    if (/\btemplate\s*:\s*[`'"]/.test(text)) issues.push({ kind: 'inline-tpl', detail: 'inline `template:` (use templateUrl)' });
    if (/\bstyles\s*:\s*\[/.test(text))      issues.push({ kind: 'inline-tpl', detail: 'inline `styles:` (use styleUrl)' });
  }

  // deep-import
  const deep = [...text.matchAll(/from\s+['"]([^'"]*features\/[^'"\/]+\/(components|state|models|services)\/[^'"]+)['"]/g)]
    .map(m => m[1])
    .filter(p => {
      // a deep import inside the SAME feature is fine
      const m = /features\/([^\/]+)\//.exec(p);
      if (!m) return false;
      return !ts.includes(`/features/${m[1]}/`);
    });
  if (deep.length > 0) issues.push({ kind: 'deep-import', detail: `${deep.length} cross-feature deep import(s)` });

  // legacy-decor
  if (/@Input\(/.test(text) || /@Output\(/.test(text)) {
    const inputs = (text.match(/@Input\(/g) ?? []).length;
    const outputs = (text.match(/@Output\(/g) ?? []).length;
    issues.push({ kind: 'legacy-decor', detail: `${inputs}× @Input + ${outputs}× @Output (Angular 21: input() / output())` });
  }

  // no-spec
  if (!spec) issues.push({ kind: 'no-spec', detail: 'no sibling .spec.ts' });

  // mouse-evt — only flag template-bound handlers (those at risk from
  // a later `(keydown.enter)="..."` binding). Strip @HostListener-
  // decorated methods first; their event type is intrinsic to the
  // listener registration, not the template, so they can't be broken
  // by binding edits.
  const stripped = text.replace(/@HostListener\([^)]*\)\s*[A-Za-z_$][\w$]*\s*\([^)]*\)\s*[:\s][^{]*\{[\s\S]*?\n\s{2}\}/g, '');
  if (/:\s*MouseEvent\b/.test(stripped)) issues.push({ kind: 'mouse-evt', detail: 'handler param typed MouseEvent (loose to Event for keyboard fallback)' });

  reports.push({ name: base, path: ts, dir, tsLoc, htmlLoc, scssLoc, total, issues });
}

const wantMd = process.argv.includes('--md');
const wantJson = process.argv.includes('--json');

if (wantJson) {
  console.log(JSON.stringify(reports, null, 2));
  process.exit(0);
}

// Default + --md: render markdown
const KINDS = ['size', 'orphan-scss', 'hex-colour', 'inline-tpl', 'deep-import', 'legacy-decor', 'no-spec', 'mouse-evt'];
const byKind = new Map(KINDS.map(k => [k, []]));
for (const r of reports) {
  for (const i of r.issues) byKind.get(i.kind).push({ ...r, detail: i.detail });
}

console.log('# Component health audit');
console.log('');
console.log(`Scanned ${reports.length} Angular components under \`${ROOT}/\`. Components with at least one issue: ${reports.filter(r => r.issues.length > 0).length}.`);
console.log('');
console.log('## Buckets (descending impact)');
console.log('');

const LABELS = {
  'size':         'Size budget — components over the 800 LOC line that the size-budget guard would flag if newly raised',
  'orphan-scss':  'Orphan SCSS — `.scss` selectors that match no element in any template (likely migration leftover, was the orchestrator-side-sheet bug)',
  'hex-colour':   'Hard-coded hex colours outside `:root` / `:host` — should resolve via `var(--studio-…)` per the design-token rule',
  'inline-tpl':   'Inline `template:` / `styles:` metadata — AGENTS.md "Component size budgets" forbids',
  'deep-import':  'Cross-feature deep imports that bypass the feature barrel (ADR-0034)',
  'legacy-decor': 'Legacy `@Input` / `@Output` decorators (Angular 21 has the signal-form `input()` / `output()`)',
  'no-spec':      'No sibling `.spec.ts` — the auto-generated smoke spec layer never ran for this component',
  'mouse-evt':    'Handler param typed `MouseEvent` — was the recent breakage when `(keydown.enter)="..."` got added by another agent',
};

for (const kind of KINDS) {
  const rows = byKind.get(kind);
  if (rows.length === 0) continue;
  console.log(`### \`${kind}\` — ${rows.length} component${rows.length === 1 ? '' : 's'}`);
  console.log('');
  console.log(`*${LABELS[kind]}*`);
  console.log('');
  rows.sort((a, b) => b.total - a.total);
  for (const r of rows.slice(0, 40)) {
    console.log(`- **${r.name}** (${r.total} LOC) — ${r.detail}`);
  }
  if (rows.length > 40) console.log(`- … and ${rows.length - 40} more`);
  console.log('');
}

console.log('## Per-component scoreboard');
console.log('');
console.log('| Component | LOC | Issues | Detail |');
console.log('|-----------|-----|--------|--------|');
const sorted = reports
  .map(r => ({ ...r, score: r.issues.length }))
  .sort((a, b) => b.score - a.score || b.total - a.total);
for (const r of sorted) {
  const kinds = r.issues.map(i => i.kind).join(', ');
  console.log(`| **${r.name}** | ${r.total} | ${r.score} | ${kinds || '—'} |`);
}
console.log('');
console.log('## Components with zero issues');
console.log('');
const clean = reports.filter(r => r.issues.length === 0).sort((a, b) => a.name.localeCompare(b.name));
console.log(`${clean.length} components are clean across all checks: ${clean.map(r => r.name).join(', ')}.`);

#!/usr/bin/env node
/**
 * Hunt for CSS class selectors defined in a component's own .scss that
 * are not referenced anywhere in any .html/.ts/.scss under src/app/.
 *
 * Origin story: the orchestrator-side-sheet's `.scss` carried ~150 lines
 * of BEM rules (`.sheet`, `.sheet__header`, `.sheet__title`,
 * `.sheet__close`, ...) that targeted DOM the component no longer
 * rendered after it was wrapped in `<app-sidesheet>`. The user asked
 * whether the same pattern hides in other components — this script
 * answers that question with a per-component list of likely orphans.
 *
 * Caveats (handled by reporting "likely orphan" rather than "remove
 * me"):
 *  - `ViewEncapsulation.None` lets a defining component leak its BEM
 *    globally, where it can match elements rendered by a different
 *    component's template. The script searches ALL .html and .ts in
 *    src/app/, so a class used anywhere is considered live.
 *  - Dynamic class names like `[class.foo]="bar"`, `[ngClass]="..."`,
 *    or template literals are matched conservatively by the literal
 *    class name appearing somewhere in the .ts.
 *  - Modifier classes like `.sheet__btn:hover` or `.foo.bar` count the
 *    base class name once; cascade-only selectors (`.foo > .bar`)
 *    surface every class.
 *  - Pseudo-selectors (`:host`, `:host(...)`, `::ng-deep`, `:not(...)`)
 *    are skipped.
 *
 * Output: a table with one row per component (sorted by orphan count)
 * plus the orphan class names, capped to keep the list scannable. Exit
 * code is always 0; this is a research tool, not a lint gate.
 */
import { readFileSync, statSync } from 'node:fs';
import { dirname, basename, relative, join } from 'node:path';
import { execSync } from 'node:child_process';

const root = 'src/app';

const allFiles = execSync(`git ls-files ${root}`, { encoding: 'utf8' })
  .split('\n')
  .filter(Boolean);

const tsFiles = allFiles.filter(f => f.endsWith('.ts') && !f.endsWith('.spec.ts') && !f.endsWith('.d.ts'));
const htmlFiles = allFiles.filter(f => f.endsWith('.html'));
const scssFiles = allFiles.filter(f => f.endsWith('.scss'));

/** Components: .ts files declaring @Component, with their sibling .scss / .html. */
const componentDirs = new Map(); // dir → { tsPath, scssPath?, htmlPath? }
for (const ts of tsFiles) {
  const text = readFileSync(ts, 'utf8');
  if (!/^\s*@Component\s*\(/m.test(text)) continue;
  const dir = dirname(ts);
  componentDirs.set(dir, {
    tsPath: ts,
    scssPath: allFiles.find(f => f.startsWith(dir + '/') && f.endsWith('.scss') && !f.endsWith('.spec.ts')),
    htmlPath: allFiles.find(f => f.startsWith(dir + '/') && f.endsWith('.html')),
    name: basename(ts).replace(/\.ts$/, '').replace(/\.component$/, ''),
  });
}

/** Build a single haystack: every .html and .ts in src/app/, lowercased.
 *  Class usage matching is `text.includes(classLiteral)` so the haystack
 *  also catches BEM-name literals appearing inside string templates or
 *  `[class.foo]` bindings without us having to parse Angular templates. */
const haystackPieces = [];
for (const f of [...htmlFiles, ...tsFiles]) {
  try { haystackPieces.push(readFileSync(f, 'utf8')); } catch { /* skip */ }
}
const haystack = haystackPieces.join('\n');

/**
 * Pull every plain `.foo-bar__baz` class selector token out of a SCSS
 * source. Skips pseudo-selectors (`:host`, `:not(...)`, `::ng-deep`,
 * etc.), `&` references, and `@` rules. Compound `.foo.bar` returns
 * both names individually.
 */
function extractScssClasses(scss) {
  const classes = new Set();
  // Strip block comments and line comments so we don't pick names out
  // of doc-prose.
  const clean = scss
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/(^|[^:])\/\/.*$/gm, '$1');
  // Match `.identifier` not preceded by an identifier char (so `.5em`
  // and `&.foo` and `:not(.x)` aren't confused; we accept both starts).
  const re = /(^|[^A-Za-z0-9_-])\.([A-Za-z][A-Za-z0-9_-]+)/g;
  let m;
  while ((m = re.exec(clean)) !== null) {
    const name = m[2];
    if (name.startsWith('ng-')) continue;       // Angular runtime, never declared
    classes.add(name);
  }
  return classes;
}

/** Is the class name used anywhere outside its defining scss? */
function classUsedAnywhere(className, definingScssPath) {
  // Cheap pre-check on the global haystack — fast common case.
  if (!haystack.includes(className)) return false;
  // The defining scss is in the haystack too. To avoid declaring its
  // own scss as a "use", build a more careful check: look for the
  // class as a token in any html or ts file. Boundary chars before/
  // after must NOT be identifier-friendly.
  const reUse = new RegExp(`(^|[^A-Za-z0-9_-])${escapeRe(className)}([^A-Za-z0-9_-]|$)`);
  for (const html of htmlFiles) {
    const txt = haystackByFile.get(html) ?? '';
    if (reUse.test(txt)) return true;
  }
  for (const ts of tsFiles) {
    const txt = haystackByFile.get(ts) ?? '';
    if (reUse.test(txt)) return true;
  }
  return false;
}

const haystackByFile = new Map();
for (const f of [...htmlFiles, ...tsFiles]) {
  try { haystackByFile.set(f, readFileSync(f, 'utf8')); } catch { /* skip */ }
}

function escapeRe(s) { return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }

const rows = [];
for (const [, info] of componentDirs) {
  if (!info.scssPath) continue;
  let scss;
  try { scss = readFileSync(info.scssPath, 'utf8'); } catch { continue; }
  const declared = extractScssClasses(scss);
  if (declared.size === 0) continue;
  const orphans = [];
  for (const cls of declared) {
    if (!classUsedAnywhere(cls, info.scssPath)) orphans.push(cls);
  }
  if (orphans.length === 0) continue;
  rows.push({
    name: info.name,
    scssPath: info.scssPath,
    declared: declared.size,
    orphan: orphans.length,
    samples: orphans.slice(0, 12),
  });
}

rows.sort((a, b) => b.orphan - a.orphan);

console.log('Components with likely-orphan CSS classes');
console.log('=========================================');
console.log('(class declared in own .scss, never referenced in any .html or .ts under src/app/)');
console.log('');
console.log('orphan / declared   component (samples)');
console.log('-----------------   -------------------');
for (const r of rows) {
  const pct = Math.round((r.orphan / r.declared) * 100);
  const head = `${String(r.orphan).padStart(4)} / ${String(r.declared).padEnd(3)} (${String(pct).padStart(3)}%)`;
  console.log(`${head}   ${r.name}`);
  console.log(`                    ${r.scssPath}`);
  console.log(`                    ${r.samples.join(', ')}${r.samples.length < r.orphan ? ', …' : ''}`);
  console.log('');
}
console.log(`Total: ${rows.length} components, ${rows.reduce((s, r) => s + r.orphan, 0)} likely-orphan classes.`);

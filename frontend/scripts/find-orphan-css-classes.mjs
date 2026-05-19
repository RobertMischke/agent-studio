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
 * **Smart filters (v2):**
 *  - *Library prefixes* — third-party class names (`hljs-`, `d2h-`,
 *    `cdk-`, `ProseMirror`, `katex`, `markdown-it-*`) are emitted under
 *    their own header so consumers can see they are deliberate
 *    cross-encapsulation styles, not orphans.
 *  - *Dynamic class detection* — if `.foo--warning` is declared in scss
 *    but the .ts builds the name via `` `foo--${tone}` ``, the literal
 *    never appears in the source. We detect this by scanning template
 *    literals and `[class.${...}]` bindings for prefix patterns like
 *    `foo--` or `foo-` followed by interpolation, then mark the
 *    matching scss classes as "likely dynamic".
 *
 * Caveats (kept from v1):
 *  - `ViewEncapsulation.None` lets a defining component leak BEM
 *    globally; the script searches ALL .html/.ts in src/app/, so a
 *    class used anywhere is considered live.
 *  - Pseudo-selectors (`:host`, `:host(...)`, `::ng-deep`, `:not(...)`)
 *    are skipped.
 *
 * Output: three sections — likely orphans, likely-dynamic (informational),
 * library prefixes (informational). Exit code is always 0; research tool,
 * not a lint gate.
 */
import { readFileSync } from 'node:fs';
import { dirname, basename } from 'node:path';
import { execSync } from 'node:child_process';

const root = 'src/app';

const allFiles = execSync(`git ls-files ${root}`, { encoding: 'utf8' })
  .split('\n')
  .filter(Boolean);

const tsFiles = allFiles.filter(f => f.endsWith('.ts') && !f.endsWith('.spec.ts') && !f.endsWith('.d.ts'));
const htmlFiles = allFiles.filter(f => f.endsWith('.html'));

const componentDirs = new Map();
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

const haystackByFile = new Map();
for (const f of [...htmlFiles, ...tsFiles]) {
  try { haystackByFile.set(f, readFileSync(f, 'utf8')); } catch { /* skip */ }
}

/** Library prefixes that third-party renderers emit; the component
 *  styles them but never authors the class literal itself. */
const LIBRARY_PREFIXES = [
  'hljs',         // highlight.js
  'd2h',          // diff2html
  'cdk',          // Angular CDK overlay/portal
  'ProseMirror',  // ProseMirror editor (capitalised)
  'prosemirror',
  'katex',        // KaTeX math
  'mat',          // Angular Material
  'mdc',          // Material Design Components
  'ngx',          // ngx-* third-party libs
  'markdown',     // markdown-it / generic markdown renderers
  'shiki',        // shiki syntax highlighter
];

function libraryPrefixOf(className) {
  for (const p of LIBRARY_PREFIXES) {
    if (className === p) return p;
    if (className.startsWith(p + '-')) return p;
    if (className.startsWith(p + '_')) return p;
  }
  return null;
}

/** Pull every plain `.foo-bar__baz` class selector token out of a SCSS
 *  source. Skips pseudo-selectors, `&` refs, and `@` rules. */
function extractScssClasses(scss) {
  const classes = new Set();
  const clean = scss
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/(^|[^:])\/\/.*$/gm, '$1');
  const re = /(^|[^A-Za-z0-9_-])\.([A-Za-z][A-Za-z0-9_-]+)/g;
  let m;
  while ((m = re.exec(clean)) !== null) {
    const name = m[2];
    if (name.startsWith('ng-')) continue;
    classes.add(name);
  }
  return classes;
}

function escapeRe(s) { return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }

/** Literal use anywhere in any html/ts file. */
function classUsedAnywhere(className) {
  const reUse = new RegExp(`(^|[^A-Za-z0-9_-])${escapeRe(className)}([^A-Za-z0-9_-]|$)`);
  for (const [, txt] of haystackByFile) {
    if (reUse.test(txt)) return true;
  }
  return false;
}

/** Collect every dynamic class-name *prefix* the codebase builds via
 *  template literal or class binding. For each pattern we record the
 *  prefix string up to the first interpolation, plus a separator hint
 *  (`-` or `--` or `__`). Later we test whether an orphan class
 *  matches one of these prefixes. */
function collectDynamicPrefixes() {
  const prefixes = new Set();
  for (const [, txt] of haystackByFile) {
    // Template literal:  `foo--${...}`   `foo-${tone}`   `pane-header__${seg}-${idx}`
    const reTpl = /`([A-Za-z][A-Za-z0-9_-]*?(?:--|__|-))\$\{/g;
    let m;
    while ((m = reTpl.exec(txt)) !== null) prefixes.add(m[1]);
    // [class.foo-${var}]  (rarer but possible)
    const reBind = /\[class\.([A-Za-z][A-Za-z0-9_-]*?(?:--|__|-))\$\{/g;
    while ((m = reBind.exec(txt)) !== null) prefixes.add(m[1]);
    // class="... foo--{{ var }}"  (interpolation in raw class attr)
    const reInterp = /class\s*=\s*"[^"]*?\b([A-Za-z][A-Za-z0-9_-]*?(?:--|__|-))\{\{/g;
    while ((m = reInterp.exec(txt)) !== null) prefixes.add(m[1]);
  }
  return prefixes;
}

const dynamicPrefixes = collectDynamicPrefixes();

function matchesDynamicPrefix(className) {
  for (const p of dynamicPrefixes) {
    if (className.startsWith(p) && className.length > p.length) return p;
  }
  return null;
}

const orphans = [];
const dynamicHits = [];
const libraryHits = [];

for (const [, info] of componentDirs) {
  if (!info.scssPath) continue;
  let scss;
  try { scss = readFileSync(info.scssPath, 'utf8'); } catch { continue; }
  const declared = extractScssClasses(scss);
  if (declared.size === 0) continue;

  const compOrphans = [];
  const compDynamic = [];
  const compLibrary = [];
  for (const cls of declared) {
    if (classUsedAnywhere(cls)) continue;
    const lib = libraryPrefixOf(cls);
    if (lib) {
      compLibrary.push({ cls, lib });
      continue;
    }
    const dyn = matchesDynamicPrefix(cls);
    if (dyn) {
      compDynamic.push({ cls, dyn });
      continue;
    }
    compOrphans.push(cls);
  }
  const base = {
    name: info.name,
    scssPath: info.scssPath,
    declared: declared.size,
  };
  if (compOrphans.length > 0) {
    orphans.push({ ...base, orphan: compOrphans.length, samples: compOrphans.slice(0, 12), total: compOrphans.length });
  }
  if (compDynamic.length > 0) {
    dynamicHits.push({ ...base, dynamic: compDynamic.length, samples: compDynamic.slice(0, 8) });
  }
  if (compLibrary.length > 0) {
    libraryHits.push({ ...base, library: compLibrary.length, samples: compLibrary.slice(0, 8) });
  }
}

orphans.sort((a, b) => b.orphan - a.orphan);
dynamicHits.sort((a, b) => b.dynamic - a.dynamic);
libraryHits.sort((a, b) => b.library - a.library);

function printOrphanTable() {
  console.log('================================================================');
  console.log('1. Likely orphan classes — declared in own .scss, never used');
  console.log('================================================================');
  console.log('');
  console.log('orphan / declared   component');
  console.log('-----------------   -------------------');
  for (const r of orphans) {
    const pct = Math.round((r.orphan / r.declared) * 100);
    const head = `${String(r.orphan).padStart(4)} / ${String(r.declared).padEnd(3)} (${String(pct).padStart(3)}%)`;
    console.log(`${head}   ${r.name}`);
    console.log(`                    ${r.scssPath}`);
    console.log(`                    ${r.samples.join(', ')}${r.samples.length < r.total ? ', …' : ''}`);
    console.log('');
  }
  console.log(`Total: ${orphans.length} components, ${orphans.reduce((s, r) => s + r.orphan, 0)} likely-orphan classes.`);
}

function printDynamicTable() {
  if (dynamicHits.length === 0) return;
  console.log('');
  console.log('================================================================');
  console.log('2. Likely-DYNAMIC classes — matched a `prefix-${…}` pattern');
  console.log('================================================================');
  console.log('(Informational. These are probably constructed in TS via template');
  console.log(' literals or interpolation; review before deleting.)');
  console.log('');
  for (const r of dynamicHits) {
    console.log(`  ${r.name}  (${r.dynamic} class(es))`);
    for (const { cls, dyn } of r.samples) {
      console.log(`    .${cls}   matches prefix \`${dyn}\${...}\``);
    }
    console.log('');
  }
}

function printLibraryTable() {
  if (libraryHits.length === 0) return;
  console.log('');
  console.log('================================================================');
  console.log('3. LIBRARY-prefix classes — styling third-party DOM');
  console.log('================================================================');
  console.log('(Informational. These target classes emitted by libraries like');
  console.log(' highlight.js, diff2html, ProseMirror, Angular CDK — keep them.)');
  console.log('');
  for (const r of libraryHits) {
    const byLib = new Map();
    for (const { cls, lib } of r.samples) {
      if (!byLib.has(lib)) byLib.set(lib, []);
      byLib.get(lib).push(cls);
    }
    console.log(`  ${r.name}  (${r.library} class(es))`);
    for (const [lib, names] of byLib) {
      console.log(`    ${lib}:  ${names.join(', ')}${names.length < r.library ? ', …' : ''}`);
    }
    console.log('');
  }
}

printOrphanTable();
printDynamicTable();
printLibraryTable();

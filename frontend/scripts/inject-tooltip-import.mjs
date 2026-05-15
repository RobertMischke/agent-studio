#!/usr/bin/env node
// Adds TooltipDirective to the `imports:` array of every standalone Angular
// component whose template contains `appTooltip`. Idempotent: skips files
// that already import it.

import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const FRONTEND = path.resolve(path.dirname(__filename), '..');
const ROOT = path.join(FRONTEND, 'src', 'app');

const TOOLTIP_BARREL = '../../components/tooltip'; // recomputed per file

async function* walk(dir) {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) yield* walk(p);
    else if (e.isFile() && p.endsWith('.ts') && !p.endsWith('.spec.ts')) yield p;
  }
}

function computeImportPath(fromFile) {
  // From: <fromFile>  →  src/app/components/tooltip
  const target = path.join(ROOT, 'components', 'tooltip');
  let rel = path.relative(path.dirname(fromFile), target).replaceAll('\\', '/');
  if (!rel.startsWith('.')) rel = './' + rel;
  return rel;
}

async function maybeInject(file) {
  const src = await fs.readFile(file, 'utf8');
  if (!/templateUrl\s*:\s*['"][^'"]+['"]/.test(src)) return null;

  // Resolve template path and check whether the template uses appTooltip.
  const tplMatch = src.match(/templateUrl\s*:\s*['"]([^'"]+)['"]/);
  if (!tplMatch) return null;
  const tplPath = path.resolve(path.dirname(file), tplMatch[1]);
  let tpl;
  try {
    tpl = await fs.readFile(tplPath, 'utf8');
  } catch {
    return null;
  }
  if (!/appTooltip/.test(tpl)) return null;

  // Already imports it?
  if (/TooltipDirective\b/.test(src)) return { file, status: 'already' };

  const importPath = computeImportPath(file);
  // Insert a new import line after the last existing import statement.
  const importLine = `import { TooltipDirective } from '${importPath}';`;
  let newSrc;
  const lastImport = [...src.matchAll(/^import .*?;\s*$/gm)].pop();
  if (lastImport) {
    const idx = lastImport.index + lastImport[0].length;
    newSrc = src.slice(0, idx) + '\n' + importLine + src.slice(idx);
  } else {
    newSrc = importLine + '\n' + src;
  }

  // Add to `imports: [...]` array. If the @Component decorator has no
  // imports array yet, insert one right after `standalone: true,`.
  const importsArrayRe = /imports\s*:\s*\[([\s\S]*?)\]/;
  const m = newSrc.match(importsArrayRe);
  if (m) {
    const inner = m[1];
    if (/\bTooltipDirective\b/.test(inner)) return { file, status: 'already-array' };
    const trimmedInner = inner.replace(/\s+$/, '');
    const newInner = trimmedInner.length === 0
      ? ' TooltipDirective '
      : `${trimmedInner}, TooltipDirective`;
    newSrc = newSrc.replace(importsArrayRe, `imports: [${newInner}]`);
  } else {
    // Insert `imports: [TooltipDirective],` after `standalone: true,` inside
    // the first @Component({...}) literal in the file.
    const standaloneRe = /(@Component\(\s*\{[\s\S]*?standalone\s*:\s*true\s*,)/;
    const sm = newSrc.match(standaloneRe);
    if (!sm) return { file, status: 'no-component-decorator' };
    newSrc = newSrc.replace(standaloneRe, `$1\n  imports: [TooltipDirective],`);
  }

  await fs.writeFile(file, newSrc, 'utf8');
  return { file, status: 'injected' };
}

async function main() {
  const results = [];
  for await (const file of walk(ROOT)) {
    const r = await maybeInject(file);
    if (r) results.push(r);
  }
  const inj = results.filter(r => r.status === 'injected');
  const already = results.filter(r => r.status === 'already' || r.status === 'already-array');
  const skipped = results.filter(r => r.status === 'no-imports-array');
  console.log(`Injected TooltipDirective into ${inj.length} component(s).`);
  for (const r of inj) console.log(`  + ${path.relative(ROOT, r.file).replaceAll('\\', '/')}`);
  if (already.length) {
    console.log(`Already imported: ${already.length}`);
  }
  if (skipped.length) {
    console.log(`Skipped (no imports: array found): ${skipped.length}`);
    for (const r of skipped) console.log(`  ? ${path.relative(ROOT, r.file).replaceAll('\\', '/')}`);
  }
}

main().catch(err => {
  console.error(err);
  process.exitCode = 1;
});

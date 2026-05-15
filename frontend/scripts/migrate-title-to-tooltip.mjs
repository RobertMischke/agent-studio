#!/usr/bin/env node
// One-shot migration: rewrite `title="..."`, `[title]="..."`, and
// `[attr.title]="..."` to `[appTooltip]="..."` across frontend/src/app/.
// Also rewrites the previous `[appTip]` selector to `[appTooltip]`.
//
// The script does NOT touch TS files. After it runs, the matching
// component.ts files still need TooltipDirective added to `imports:`.

import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const ROOT = path.resolve(path.dirname(__filename), '..', 'src', 'app');

async function* walk(dir) {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) yield* walk(p);
    else if (e.isFile() && p.endsWith('.html')) yield p;
  }
}

function migrateHtml(src) {
  let s = src;
  let changes = 0;

  // [appTip]="..." → [appTooltip]="..."
  s = s.replace(/\[appTip\]=/g, () => { changes++; return '[appTooltip]='; });

  // [title]="..." → [appTooltip]="..." (preserve whatever the expression is)
  s = s.replace(/\[title\]="([^"]*)"/g, (_m, expr) => {
    changes++;
    return `[appTooltip]="${expr}"`;
  });

  // [attr.title]="..." → [appTooltip]="..."
  s = s.replace(/\[attr\.title\]="([^"]*)"/g, (_m, expr) => {
    changes++;
    return `[appTooltip]="${expr}"`;
  });

  // title="literal" → [appTooltip]="'literal'"
  // The string literal needs single quotes, and any single quote inside the
  // literal must be escaped. HTML entities (&amp;, &lt;, &gt;, &quot;, &#39;)
  // are left as-is; the tooltip controller's innerHTML path decodes them.
  s = s.replace(/\btitle="([^"]*)"/g, (_m, literal) => {
    changes++;
    const escaped = literal.replace(/'/g, "\\'");
    return `[appTooltip]="'${escaped}'"`;
  });

  return { out: s, changes };
}

async function main() {
  let totalChanges = 0;
  let totalFiles = 0;
  const touched = [];
  for await (const file of walk(ROOT)) {
    const src = await fs.readFile(file, 'utf8');
    const { out, changes } = migrateHtml(src);
    if (changes > 0) {
      await fs.writeFile(file, out, 'utf8');
      totalChanges += changes;
      totalFiles += 1;
      touched.push({ file: path.relative(ROOT, file).replaceAll('\\', '/'), changes });
    }
  }
  console.log(`Migrated ${totalChanges} attribute(s) across ${totalFiles} file(s):`);
  for (const t of touched) console.log(`  ${t.changes.toString().padStart(3)} - ${t.file}`);
}

main().catch(err => {
  console.error(err);
  process.exitCode = 1;
});

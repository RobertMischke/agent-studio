#!/usr/bin/env node
/**
 * Cycle 11a/b helper. Walks a list of .ts files, finds the @Component
 * decorator, extracts the inline `template:` and `styles:` blocks into
 * sibling `.html` / `.scss` files, and rewrites the decorator to use
 * `templateUrl` + `styleUrl`.
 *
 * Safety:
 * - Bails on any file where the decorator shape is unfamiliar (no
 *   automatic guess-and-pray).
 * - Preserves CRLF line endings (Windows repo).
 * - Refuses to overwrite an existing .html / .scss with different
 *   content.
 *
 * Usage:
 *   node scripts/extract-inline-templates.mjs file1.ts file2.ts ...
 *   node scripts/extract-inline-templates.mjs --dry-run file1.ts
 */
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, basename, join } from 'node:path';

const args = process.argv.slice(2);
const dryRun = args.includes('--dry-run');
const files = args.filter(a => a !== '--dry-run');

if (files.length === 0) {
  console.error('Usage: extract-inline-templates.mjs [--dry-run] file1.ts ...');
  process.exit(2);
}

let okCount = 0;
let skipCount = 0;
const errors = [];

for (const file of files) {
  try {
    const result = extract(file, dryRun);
    if (result.skipped) {
      skipCount++;
      console.log(`SKIP  ${file}  (${result.reason})`);
    } else {
      okCount++;
      const htmlBit = result.htmlPath ? basename(result.htmlPath) : '(no template)';
      const scssBit = result.scssPath ? basename(result.scssPath) : '(no styles)';
      console.log(`OK    ${file}  -> ${htmlBit} + ${scssBit}`);
    }
  } catch (e) {
    errors.push({ file, message: e.message });
    console.error(`FAIL  ${file}  ${e.message}`);
  }
}

console.log(`\nSummary: ${okCount} extracted, ${skipCount} skipped, ${errors.length} failed`);
if (errors.length > 0) process.exit(1);

function extract(file, dryRun) {
  const raw = readFileSync(file, 'utf8');
  const eol = raw.includes('\r\n') ? '\r\n' : '\n';

  // Find @Component({ ... }) decorator.
  const decoMatch = raw.match(/@Component\s*\(\s*\{/);
  if (!decoMatch) return { skipped: true, reason: 'no @Component decorator' };

  const decoStart = decoMatch.index + decoMatch[0].length - 1; // index of '{'
  const decoBody = matchBalanced(raw, decoStart, '{', '}');
  if (!decoBody) return { skipped: true, reason: 'unbalanced decorator' };

  const decoText = raw.slice(decoBody.start + 1, decoBody.end);

  // Locate template: `...`  (may be absent if the file already uses templateUrl)
  const tpl = findBacktickKey(decoText, 'template');

  // Locate styles: [`...`, `...`] or styles: [`...`]  (also tolerate a single backtick string)
  const stylesArr = findStylesArray(decoText);

  if (!tpl && !stylesArr) {
    return { skipped: true, reason: 'no inline template: or styles: backtick blocks found' };
  }

  const htmlPath = file.replace(/\.ts$/, '.html');
  const scssPath = file.replace(/\.ts$/, '.scss');

  const templateContent = tpl ? unescapeBacktickString(tpl.content) : null;
  const trimmedHtml = templateContent ? trimLeadingBlankLines(dedentLikely(templateContent)) : null;

  let stylesContent = null;
  if (stylesArr) {
    stylesContent = stylesArr.contents
      .map(c => unescapeBacktickString(c))
      .map(c => trimLeadingBlankLines(dedentLikely(c)))
      .join('\n\n');
  }

  // Build new decorator body: replace template: `...` with templateUrl: './name.html'
  // and styles: [...] with styleUrl: './name.scss'.
  const baseName = basename(file).replace(/\.ts$/, '');
  let newDecoText = decoText;

  // Replace template block (template:` ... `, may have trailing comma)
  if (tpl) {
    const tplRange = tpl.fullRange;
    const tplReplacement = `templateUrl: './${baseName}.html'` + (tpl.hadTrailingComma ? ',' : '');
    newDecoText =
      newDecoText.slice(0, tplRange[0]) +
      tplReplacement +
      newDecoText.slice(tplRange[1]);
  }

  if (stylesArr) {
    // Recompute the styles array range against the mutated text. Because we
    // already replaced the template block, indices shifted — re-locate.
    const stylesArrAfter = findStylesArray(newDecoText);
    if (!stylesArrAfter) throw new Error('styles array vanished after template replace');
    const styleReplacement = `styleUrl: './${baseName}.scss'` + (stylesArrAfter.hadTrailingComma ? ',' : '');
    newDecoText =
      newDecoText.slice(0, stylesArrAfter.fullRange[0]) +
      styleReplacement +
      newDecoText.slice(stylesArrAfter.fullRange[1]);
  }

  const newRaw =
    raw.slice(0, decoBody.start + 1) +
    newDecoText +
    raw.slice(decoBody.end);

  // Sanity check: must still contain templateUrl + (styleUrl if styles existed).
  if (tpl && !newRaw.includes(`templateUrl: './${baseName}.html'`)) {
    throw new Error('post-write sanity failed — templateUrl not present');
  }
  if (stylesArr && !newRaw.includes(`styleUrl: './${baseName}.scss'`)) {
    throw new Error('post-write sanity failed — styleUrl not present');
  }

  if (dryRun) {
    return { skipped: false, htmlPath: tpl ? htmlPath : null, scssPath: stylesArr ? scssPath : null, dryRun: true };
  }

  // Write outputs (CRLF for the source ts, LF or CRLF matching for html/scss).
  if (tpl) {
    const htmlOut = ensureEol(trimmedHtml, eol).replace(/(\r?\n)+$/, '') + eol;
    if (existsSync(htmlPath)) {
      const existing = readFileSync(htmlPath, 'utf8');
      if (existing !== htmlOut) throw new Error(`refuses to overwrite existing ${htmlPath}`);
    } else {
      writeFileSync(htmlPath, htmlOut);
    }
  }

  if (stylesArr) {
    const scssOut = ensureEol(stylesContent, eol).replace(/(\r?\n)+$/, '') + eol;
    if (existsSync(scssPath)) {
      const existing = readFileSync(scssPath, 'utf8');
      if (existing !== scssOut) throw new Error(`refuses to overwrite existing ${scssPath}`);
    } else {
      writeFileSync(scssPath, scssOut);
    }
  }

  writeFileSync(file, ensureEol(newRaw, eol));
  return { skipped: false, htmlPath, scssPath: stylesArr ? scssPath : null };
}

// --- helpers ---

function matchBalanced(text, openIdx, open, close) {
  if (text[openIdx] !== open) return null;
  let depth = 0;
  for (let i = openIdx; i < text.length; i++) {
    const c = text[i];
    if (c === '/' && text[i + 1] === '/') {
      const nl = text.indexOf('\n', i);
      i = nl === -1 ? text.length : nl;
      continue;
    }
    if (c === '/' && text[i + 1] === '*') {
      const end = text.indexOf('*/', i + 2);
      i = end === -1 ? text.length : end + 1;
      continue;
    }
    if (c === '`') {
      // skip backtick string including escapes & ${...}
      i = skipBacktick(text, i);
      continue;
    }
    if (c === "'" || c === '"') {
      i = skipQuoted(text, i, c);
      continue;
    }
    if (c === open) depth++;
    else if (c === close) {
      depth--;
      if (depth === 0) return { start: openIdx, end: i };
    }
  }
  return null;
}

function skipBacktick(text, start) {
  // text[start] === '`'
  let i = start + 1;
  while (i < text.length) {
    const c = text[i];
    if (c === '\\') { i += 2; continue; }
    if (c === '$' && text[i + 1] === '{') {
      // template-literal interpolation — depth-track braces
      let depth = 1;
      i += 2;
      while (i < text.length && depth > 0) {
        const cc = text[i];
        if (cc === '{') depth++;
        else if (cc === '}') depth--;
        else if (cc === '`') i = skipBacktick(text, i);
        i++;
      }
      continue;
    }
    if (c === '`') return i;
    i++;
  }
  return text.length;
}

function skipQuoted(text, start, quote) {
  let i = start + 1;
  while (i < text.length) {
    const c = text[i];
    if (c === '\\') { i += 2; continue; }
    if (c === quote) return i;
    if (c === '\n') return i; // unterminated — bail
    i++;
  }
  return text.length;
}

function findBacktickKey(text, key) {
  // Match `key:` followed by optional whitespace then a backtick.
  const re = new RegExp(`\\b${key}\\s*:\\s*\``);
  const m = text.match(re);
  if (!m) return null;
  const tickStart = m.index + m[0].length - 1;
  const tickEnd = skipBacktick(text, tickStart);
  if (tickEnd === text.length) return null;
  // Optional trailing comma to consume.
  let endExclusive = tickEnd + 1;
  let hadTrailingComma = false;
  if (text[endExclusive] === ',') { endExclusive++; hadTrailingComma = true; }
  return {
    content: text.slice(tickStart + 1, tickEnd),
    fullRange: [m.index, endExclusive],
    hadTrailingComma,
  };
}

function findStylesArray(text) {
  // styles: [ `...`, `...` ]
  const re = /\bstyles\s*:\s*\[/;
  const m = text.match(re);
  if (!m) {
    // also tolerate `styles: \`...\`` (single string, no array)
    const single = findBacktickKey(text, 'styles');
    if (!single) return null;
    return {
      contents: [single.content],
      fullRange: single.fullRange,
      hadTrailingComma: single.hadTrailingComma,
    };
  }
  const arrStart = m.index + m[0].length - 1; // index of '['
  const balanced = matchBalanced(text, arrStart, '[', ']');
  if (!balanced) return null;
  const inside = text.slice(balanced.start + 1, balanced.end);
  // Pull out top-level backtick strings inside.
  const contents = [];
  let i = 0;
  while (i < inside.length) {
    const c = inside[i];
    if (c === '`') {
      const end = skipBacktick(inside, i);
      contents.push(inside.slice(i + 1, end));
      i = end + 1;
    } else {
      i++;
    }
  }
  if (contents.length === 0) return null;
  let endExclusive = balanced.end + 1;
  let hadTrailingComma = false;
  if (text[endExclusive] === ',') { endExclusive++; hadTrailingComma = true; }
  return {
    contents,
    fullRange: [m.index, endExclusive],
    hadTrailingComma,
  };
}

function unescapeBacktickString(s) {
  // Backtick strings preserve literal characters except for `\\`, `\``, `${`.
  // For Angular templates we expect raw HTML — only `\\\`` -> '`' and `\\\\`
  // -> '\\' need handling. Most files have no escapes at all.
  return s.replace(/\\`/g, '`').replace(/\\\$/g, '$').replace(/\\\\/g, '\\');
}

function dedentLikely(s) {
  // Find min indent of non-empty lines and strip it. Preserves relative indent.
  const lines = s.split('\n');
  let min = Infinity;
  for (const line of lines) {
    if (!line.trim()) continue;
    const indent = line.match(/^[ \t]*/)[0].length;
    if (indent < min) min = indent;
  }
  if (!isFinite(min) || min === 0) return s;
  return lines.map(l => (l.trim() ? l.slice(min) : l)).join('\n');
}

function trimLeadingBlankLines(s) {
  return s.replace(/^(\r?\n)+/, '');
}

function ensureEol(s, eol) {
  // Normalize all line endings to the requested EOL.
  return s.replace(/\r\n|\r|\n/g, eol);
}

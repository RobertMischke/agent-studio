#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');
const docsRoot = path.join(repoRoot, 'docs');
const legacyMetadataDir = path.join(docsRoot, 'meta', 'documents');
const schemaId = 'https://agent-taskboard.local/schemas/wiki-document-companion.schema.json';
const generatorPath = 'scripts/wiki/generate-companion-metadata.mjs';
const capturedAt = process.env.WIKI_COMPANION_CAPTURED_AT || new Date().toISOString();
const reviewDate = capturedAt.slice(0, 10);

const reportableExtensions = new Set(['.md', '.html', '.htm', '.json']);

function walk(dir, predicate, files = []) {
  if (!existsSync(dir)) return files;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name.startsWith('.')) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(full, predicate, files);
    } else if (predicate(full)) {
      files.push(full);
    }
  }
  return files;
}

function toPosix(value) {
  return value.replace(/\\/g, '/');
}

function docsRelative(absPath) {
  return toPosix(path.relative(docsRoot, absPath));
}

function repoRelative(absPath) {
  return toPosix(path.relative(repoRoot, absPath));
}

function normalizeDocsPath(value) {
  if (!value || typeof value !== 'string') return null;
  let rel = toPosix(value.trim());
  while (rel.startsWith('./')) rel = rel.slice(2);
  if (rel.startsWith('docs/')) rel = rel.slice(5);
  if (!rel || rel.includes('..') || rel.startsWith('/')) return null;
  return rel;
}

function sidecarSourceRel(absPath) {
  const rel = docsRelative(absPath);
  return rel.endsWith('.meta.json') ? rel.slice(0, -'.meta.json'.length) : null;
}

function readJson(file) {
  return JSON.parse(readFileSync(file, 'utf8'));
}

function sourceFingerprint(sourceAbs) {
  const content = readFileSync(sourceAbs);
  const text = content.toString('utf8');
  const sizeBytes = statSync(sourceAbs).size;
  return {
    algorithm: 'sha256',
    hash: createHash('sha256').update(content).digest('hex'),
    sizeBytes,
    lineCount: text.length === 0 ? 0 : text.split(/\r?\n/).length,
    capturedAt,
  };
}

function normalizeLegacyRecord(record, sourceRel) {
  const classification = record.classification ?? {};
  return {
    title: record.title ?? titleFromPath(sourceRel),
    owner: classification.owner ?? record.owner ?? ownerFromPath(sourceRel),
    documentMode: classification.documentMode ?? record.documentMode ?? 'documentation',
    temporalState: classification.temporalState ?? record.temporalState ?? 'mixed',
    implementationState: classification.implementationState ?? record.implementationState ?? 'unknown',
    lastReview: record.lastReview ?? record.review ?? null,
    drift: record.drift ?? null,
    axes: record.axes ?? null,
    duplicates: normalizeDuplicates(record.duplicates),
    nextAction: record.nextAction ?? null,
    findings: Array.isArray(record.findings) ? record.findings : [],
  };
}

function titleFromPath(sourceRel) {
  const base = path.basename(sourceRel, path.extname(sourceRel));
  return base
    .replace(/^\d+[-_.\s]+/, '')
    .replace(/[-_]+/g, ' ')
    .replace(/\b\w/g, char => char.toUpperCase());
}

function ownerFromPath(sourceRel) {
  return sourceRel.split('/')[0] || 'docs';
}

function documentType(sourceRel) {
  const ext = path.extname(sourceRel).toLowerCase();
  if (ext === '.md') return 'markdown';
  if (ext === '.html' || ext === '.htm') return 'html';
  if (ext === '.json') return 'json';
  return 'document';
}

function normalizeFindings(record) {
  const findings = record.findings.filter(finding => {
    const id = String(finding.id ?? '').toLowerCase();
    const axis = String(finding.axis ?? '').toLowerCase();
    return id !== 'duplicate-candidate' && axis !== 'duplicate';
  });
  const drift = record.drift;
  if (drift?.summary) {
    findings.push({
      id: 'drift-summary',
      severity: drift.hasDrift ? 'warn' : 'info',
      axis: 'drift',
      summary: drift.summary,
      rationale: Array.isArray(drift.rationale) ? drift.rationale : [],
    });
  }
  if (Array.isArray(drift?.rationale)) {
    for (const [index, item] of drift.rationale.entries()) {
      findings.push({
        id: `drift-rationale-${index + 1}`,
        severity: drift.hasDrift ? 'warn' : 'info',
        axis: 'drift',
        summary: item,
        rationale: [],
      });
    }
  }
  if (record.duplicates?.suspected) {
    findings.push({
      id: 'duplicate-candidate',
      severity: 'warn',
      axis: 'duplicate',
      summary: 'This document has possible duplicate ownership candidates.',
      rationale: Array.isArray(record.duplicates.similarTo) ? record.duplicates.similarTo : [],
    });
  }
  return dedupeFindings(findings);
}

function normalizeDuplicates(value) {
  if (!value || typeof value !== 'object') {
    return {
      suspected: false,
      groupSize: 1,
      similarTo: [],
    };
  }
  const similarTo = Array.isArray(value.similarTo) ? value.similarTo.map(String) : [];
  const parsedGroupSize = Number(value.groupSize);
  return {
    suspected: value.suspected === true,
    groupSize: Number.isFinite(parsedGroupSize) && parsedGroupSize >= 1
      ? Math.round(parsedGroupSize)
      : Math.max(1, similarTo.length + 1),
    similarTo,
  };
}

function pruneDuplicateTargets(duplicates) {
  const similarTo = duplicates.similarTo
    .map(item => normalizeDocsPath(item))
    .filter(rel => rel && existsSync(path.join(docsRoot, rel)))
    .map(rel => `docs/${rel}`);
  return {
    suspected: duplicates.suspected && similarTo.length > 0,
    groupSize: similarTo.length > 0 ? similarTo.length + 1 : 1,
    similarTo,
  };
}

function dedupeFindings(findings) {
  const seen = new Set();
  const result = [];
  for (const finding of findings) {
    const id = String(finding.id ?? finding.summary ?? '').trim() || `finding-${result.length + 1}`;
    const summary = String(finding.summary ?? '').trim();
    const key = `${id}\0${summary}`;
    if (!summary || seen.has(key)) continue;
    seen.add(key);
    result.push({
      id,
      severity: normalizeSeverity(finding.severity),
      axis: String(finding.axis ?? 'general'),
      summary,
      rationale: Array.isArray(finding.rationale) ? finding.rationale.map(String) : [],
      evidence: Array.isArray(finding.evidence) ? finding.evidence.map(String) : [],
    });
  }
  return result;
}

function normalizeSeverity(value) {
  const clean = String(value ?? '').toLowerCase();
  return ['info', 'warn', 'error'].includes(clean) ? clean : 'info';
}

function buildCompanion(seed, sourceRel) {
  const sourceAbs = path.join(docsRoot, sourceRel);
  if (!existsSync(sourceAbs)) throw new Error(`Source missing: docs/${sourceRel}`);
  if (!reportableExtensions.has(path.extname(sourceRel).toLowerCase())) {
    throw new Error(`Unsupported source extension: docs/${sourceRel}`);
  }

  const record = normalizeLegacyRecord(seed, sourceRel);
  const fingerprint = sourceFingerprint(sourceAbs);
  const duplicates = pruneDuplicateTargets(record.duplicates);
  const findings = normalizeFindings({ ...record, duplicates });
  const review = {
    date: record.lastReview?.date ?? reviewDate,
    method: record.lastReview?.method ?? 'wiki companion metadata generation',
    model: record.lastReview?.model ?? 'codex',
    sourceFingerprint: fingerprint,
    sourceChangedSinceReview: false,
  };
  const reportRel = `${sourceRel}.report.html`;

  return {
    $schema: schemaId,
    schemaVersion: 'wiki-document-companion/v1',
    title: record.title,
    source: {
      path: `docs/${sourceRel}`,
      type: documentType(sourceRel),
      fingerprint,
    },
    report: {
      path: `docs/${reportRel}`,
      generatedAt: capturedAt,
      generator: generatorPath,
      template: 'wiki-document-companion-report/v1',
    },
    classification: {
      owner: record.owner,
      documentMode: record.documentMode,
      temporalState: record.temporalState,
      implementationState: record.implementationState,
    },
    review,
    drift: record.drift ?? {
      grade: 'B',
      hasDrift: null,
      score: null,
      summary: 'No drift review summary has been generated yet.',
      rationale: [],
    },
    axes: record.axes ?? {},
    duplicates,
    findings,
    nextAction: record.nextAction ?? 'Review during the next document drift pass.',
  };
}

function legacySeeds() {
  if (!existsSync(legacyMetadataDir)) return [];
  return readdirSync(legacyMetadataDir)
    .filter(name => name.endsWith('.metadata.json'))
    .map(name => {
      const full = path.join(legacyMetadataDir, name);
      const record = readJson(full);
      const sourceRel = normalizeDocsPath(record.source?.path ?? record.sourcePath);
      return sourceRel ? { sourceRel, record, source: repoRelative(full) } : null;
    })
    .filter(Boolean);
}

function sidecarSeeds() {
  return walk(docsRoot, file => file.endsWith('.meta.json'))
    .map(full => {
      const record = readJson(full);
      const sourceRel = normalizeDocsPath(record.source?.path ?? record.sourcePath) ?? sidecarSourceRel(full);
      return sourceRel ? { sourceRel, record, source: repoRelative(full) } : null;
    })
    .filter(Boolean);
}

function collectSeeds() {
  const bySource = new Map();
  for (const seed of legacySeeds()) bySource.set(seed.sourceRel, seed);
  for (const seed of sidecarSeeds()) bySource.set(seed.sourceRel, seed);
  return [...bySource.values()].sort((a, b) => a.sourceRel.localeCompare(b.sourceRel));
}

function escapeHtml(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

function gradeTone(grade) {
  const clean = String(grade ?? '').toUpperCase();
  if (clean === 'A') return 'good';
  if (clean === 'B') return 'info';
  if (clean === 'C') return 'warn';
  if (clean === 'D') return 'bad';
  return 'muted';
}

function axisRows(axes) {
  const entries = Object.entries(axes ?? {});
  if (entries.length === 0) return '<p class="muted">No axis scores were recorded yet.</p>';
  return entries.map(([key, value]) => `
        <tr><th>${escapeHtml(labelize(key))}</th><td>${escapeHtml(value)}</td></tr>`).join('');
}

function labelize(value) {
  return String(value)
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/[-_]+/g, ' ')
    .replace(/\b\w/g, char => char.toUpperCase());
}

function findingsHtml(findings) {
  if (!findings.length) return '<p class="muted">No findings were attached in the companion JSON.</p>';
  return findings.map(finding => `
        <article class="finding finding--${escapeHtml(finding.severity)}">
          <div class="finding__head">
            <span>${escapeHtml(labelize(finding.axis))}</span>
            <strong>${escapeHtml(finding.severity.toUpperCase())}</strong>
          </div>
          <p>${escapeHtml(finding.summary)}</p>
          ${finding.rationale.length ? `<ul>${finding.rationale.map(item => `<li>${escapeHtml(item)}</li>`).join('')}</ul>` : ''}
        </article>`).join('');
}

function buildReport(companion) {
  const drift = companion.drift ?? {};
  const classification = companion.classification;
  const source = companion.source;
  const review = companion.review;
  const duplicate = companion.duplicates ?? {};
  const grade = String(drift.grade ?? '?').toUpperCase();
  const tone = gradeTone(grade);
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${escapeHtml(companion.title)} - Companion Report</title>
  <style>
    :root { color-scheme: light; --ink: #172033; --muted: #64748b; --line: #dbe4ef; --soft: #f7fafc; --panel: #fff; --good: #15803d; --info: #2563eb; --warn: #b45309; --bad: #b42318; }
    * { box-sizing: border-box; }
    body { margin: 0; background: #fff; color: var(--ink); font: 14px/1.55 Inter, "Segoe UI", Arial, sans-serif; }
    main { max-width: 1120px; margin: 0 auto; padding: 28px; display: grid; gap: 18px; }
    h1, h2, h3, p { margin: 0; }
    h1 { font-size: 1.5rem; line-height: 1.2; }
    h2 { font-size: .82rem; letter-spacing: .08em; text-transform: uppercase; color: #475569; }
    code { color: #334155; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
    header { display: grid; gap: 14px; padding-bottom: 16px; border-bottom: 1px solid var(--line); }
    .hero { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 18px; align-items: end; }
    .path { color: var(--muted); }
    .scoreboard { display: grid; grid-template-columns: repeat(4, minmax(94px, 1fr)); gap: 8px; min-width: 430px; }
    .score { border: 1px solid var(--line); border-radius: 9px; background: var(--soft); padding: 10px 12px; }
    .score strong { display: block; font-size: 1.05rem; }
    .score span { color: var(--muted); font-size: .76rem; }
    .score--${tone} strong { color: var(--${tone}); }
    .grid { display: grid; grid-template-columns: minmax(0, 1.12fr) minmax(280px, .88fr); gap: 16px; }
    section { border: 1px solid var(--line); border-radius: 12px; background: var(--panel); overflow: hidden; }
    section > h2 { padding: 12px 14px; border-bottom: 1px solid var(--line); background: #f8fafc; }
    .body { padding: 14px; display: grid; gap: 12px; }
    .callout { border-left: 3px solid var(--${tone}); background: var(--soft); border-radius: 8px; padding: 12px; }
    .muted { color: var(--muted); }
    table { border-collapse: collapse; width: 100%; }
    th, td { border-bottom: 1px solid var(--line); padding: 7px 0; text-align: left; vertical-align: top; }
    th { width: 42%; color: #475569; font-weight: 650; }
    .finding { border: 1px solid var(--line); border-radius: 9px; padding: 10px; display: grid; gap: 8px; }
    .finding__head { display: flex; justify-content: space-between; gap: 10px; font-size: .78rem; color: var(--muted); }
    .finding--warn { border-color: #f2c27b; }
    .finding--error { border-color: #f0a5a5; }
    ul { margin: 0; padding-left: 18px; }
    footer { color: var(--muted); font-size: .78rem; }
    @media (max-width: 880px) { main { padding: 18px; } .hero, .grid { grid-template-columns: 1fr; } .scoreboard { min-width: 0; grid-template-columns: repeat(2, minmax(0, 1fr)); } }
  </style>
</head>
<body>
<main>
  <header>
    <div class="hero">
      <div>
        <h1>${escapeHtml(companion.title)}</h1>
        <p class="path"><code>${escapeHtml(source.path)}</code></p>
      </div>
      <div class="scoreboard" aria-label="Companion score summary">
        <div class="score score--${tone}"><strong>${escapeHtml(grade)}</strong><span>drift grade</span></div>
        <div class="score"><strong>${escapeHtml(directionLabel(classification.temporalState))}</strong><span>direction</span></div>
        <div class="score"><strong>${escapeHtml(statusLabel(classification.implementationState))}</strong><span>implementation</span></div>
        <div class="score"><strong>${review.sourceChangedSinceReview ? 'Changed' : 'Current'}</strong><span>source fingerprint</span></div>
      </div>
    </div>
  </header>

  <div class="grid">
    <section>
      <h2 id="why-drift">Why drift?</h2>
      <div class="body">
        <div class="callout">
          <p>${escapeHtml(drift.summary ?? 'No drift summary was recorded yet.')}</p>
        </div>
        ${Array.isArray(drift.rationale) && drift.rationale.length ? `<ul>${drift.rationale.map(item => `<li>${escapeHtml(item)}</li>`).join('')}</ul>` : '<p class="muted">No drift rationale was recorded yet.</p>'}
      </div>
    </section>

    <section>
      <h2 id="temporal-reasoning">Temporal reasoning</h2>
      <div class="body">
        <p>This document is classified as <strong>${escapeHtml(directionLabel(classification.temporalState))}</strong> and <strong>${escapeHtml(statusLabel(classification.implementationState))}</strong>.</p>
        <p class="muted">The companion JSON stores the source fingerprint used for this review. If the source hash changes, the next generator run can mark this report stale.</p>
      </div>
    </section>

    <section>
      <h2 id="findings">Findings</h2>
      <div class="body">
        ${findingsHtml(companion.findings ?? [])}
      </div>
    </section>

    <section>
      <h2 id="axis-evidence">Axis evidence</h2>
      <div class="body">
        <table>
          <tbody>${axisRows(companion.axes)}</tbody>
        </table>
      </div>
    </section>

    <section>
      <h2 id="duplicate-reasoning">Duplicate reasoning</h2>
      <div class="body">
        <p>${duplicate.suspected ? 'Duplicate ownership is suspected.' : 'No duplicate ownership is currently suspected.'}</p>
        <p class="muted">Group size: ${escapeHtml(duplicate.groupSize ?? 1)}</p>
      </div>
    </section>

    <section>
      <h2 id="source-fingerprint">Source fingerprint</h2>
      <div class="body">
        <table>
          <tbody>
            <tr><th>Algorithm</th><td>${escapeHtml(source.fingerprint.algorithm)}</td></tr>
            <tr><th>Hash</th><td><code>${escapeHtml(source.fingerprint.hash)}</code></td></tr>
            <tr><th>Size</th><td>${escapeHtml(source.fingerprint.sizeBytes)} bytes</td></tr>
            <tr><th>Lines</th><td>${escapeHtml(source.fingerprint.lineCount)}</td></tr>
            <tr><th>Captured</th><td>${escapeHtml(source.fingerprint.capturedAt)}</td></tr>
          </tbody>
        </table>
      </div>
    </section>
  </div>
  <footer>Generated from <code>${escapeHtml(source.path)}.meta.json</code> by <code>${generatorPath}</code>.</footer>
</main>
</body>
</html>
`;
}

function directionLabel(value) {
  switch (String(value ?? '').toLowerCase()) {
    case 'present':
    case 'current':
    case 'now':
      return 'Current';
    case 'future':
    case 'planned':
    case 'vision':
      return 'Future';
    case 'past':
    case 'historic':
    case 'obsolete':
      return 'Past';
    case 'mixed':
    case 'transition':
      return 'Mixed';
    default:
      return 'Unknown';
  }
}

function statusLabel(value) {
  switch (String(value ?? '').toLowerCase()) {
    case 'implemented':
      return 'Done';
    case 'partial':
    case 'partially-implemented':
      return 'Partial';
    case 'planned':
      return 'Planned';
    case 'aspirational':
      return 'Vision';
    default:
      return value ? labelize(value) : 'Unknown';
  }
}

function writeGenerated(companion, sourceRel) {
  const metaAbs = path.join(docsRoot, `${sourceRel}.meta.json`);
  const reportAbs = path.join(docsRoot, `${sourceRel}.report.html`);
  mkdirSync(path.dirname(metaAbs), { recursive: true });
  writeFileSync(metaAbs, `${JSON.stringify(companion, null, 2)}\n`);
  writeFileSync(reportAbs, buildReport(companion));
  return { metaAbs, reportAbs };
}

const seeds = collectSeeds();
if (seeds.length === 0) {
  console.error('No metadata seeds found. Add adjacent *.meta.json files or legacy docs/meta/documents/*.metadata.json first.');
  process.exitCode = 1;
} else {
  let written = 0;
  for (const seed of seeds) {
    const companion = buildCompanion(seed.record, seed.sourceRel);
    const { metaAbs, reportAbs } = writeGenerated(companion, seed.sourceRel);
    written += 2;
    console.log(`wrote ${repoRelative(metaAbs)} and ${repoRelative(reportAbs)}`);
  }
  console.log(`Generated ${written} companion artifacts for ${seeds.length} document(s).`);
}

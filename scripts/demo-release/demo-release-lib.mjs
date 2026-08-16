import { createHash } from 'node:crypto';
import {
  existsSync,
  readFileSync,
  readdirSync,
  statSync,
} from 'node:fs';
import { relative, resolve, sep } from 'node:path';
import { inflateSync } from 'node:zlib';

export const RELEASE_SCHEMA_VERSION = 1;
export const SCRUB_REPORT_SCHEMA_VERSION = 1;
export const GENERATOR_VERSION = 'demo-seed-generator/v1';

const IMAGE_EXTENSIONS = new Set(['.png']);
const BINARY_EXTENSIONS = new Set([
  '.gz', '.zip', '.woff', '.woff2', '.ttf', '.dll', '.exe', '.so', '.dylib',
]);

const SECRET_PATTERNS = [
  ['private-key', /-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----/giu],
  ['aws-access-key', /\b(?:AKIA|ASIA)[A-Z0-9]{16}\b/gu],
  ['github-token', /\b(?:gh[opusr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})\b/gu],
  ['jwt', /\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b/gu],
  ['assigned-secret', /\b(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)\s*[:=]\s*["']?[^\s"']{12,}/giu],
];

const DISCLOSURE_PATTERNS = [
  ['email', /\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b/giu],
  ['url', /\b(?:https?|ssh):\/\/[^\s<>"')]+/giu],
  ['repository-remote', /\b(?:git@[A-Za-z0-9.-]+:[^\s]+|(?:https?|ssh):\/\/[^\s]+\.git)\b/giu],
  ['ipv4', /\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}\b/gu],
  ['ipv6', /\b(?:[A-F0-9]{1,4}:){2,7}[A-F0-9]{1,4}\b/giu],
  ['windows-absolute-path', /\b[A-Z]:[\\/](?:Users|Documents and Settings|Projects|ProgramData|Windows)[\\/][^\s<>"']+/giu],
  ['unix-absolute-path', /(?:^|[\s("'])\/(?:home|Users|root|tmp|var|opt|srv|mnt)\/[^\s<>"')]+/gmu],
  ['user-home-fragment', /(?:\bUsers[\\/][A-Za-z0-9._-]+|\bhome\/[A-Za-z0-9._-]+)/giu],
  ['hostname', /\b[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?(?:\.[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?)*\.(?:com|net|org|io|dev|cloud|app|internal|local)\b/giu],
];

function extension(path) {
  const index = path.lastIndexOf('.');
  return index < 0 ? '' : path.slice(index).toLowerCase();
}

export function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

export function sha256File(path) {
  return sha256(readFileSync(path));
}

export function normalizedRelative(root, path) {
  const value = relative(resolve(root), resolve(path)).split(sep).join('/');
  if (!value || value === '..' || value.startsWith('../') || value.startsWith('/')) {
    throw new Error(`Path is outside the declared root: ${path}`);
  }
  return value;
}

export function listFiles(root) {
  const files = [];
  function walk(directory) {
    for (const entry of readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
      const path = resolve(directory, entry.name);
      if (entry.isSymbolicLink()) throw new Error(`Generated datastore contains a symbolic link: ${normalizedRelative(root, path)}`);
      if (entry.isDirectory()) walk(path);
      else if (entry.isFile()) files.push(path);
      else throw new Error(`Generated datastore contains an unsupported entry: ${normalizedRelative(root, path)}`);
    }
  }
  walk(resolve(root));
  return files;
}

export function classifyFile(path) {
  const ext = extension(path);
  if (IMAGE_EXTENSIONS.has(ext)) return 'image';
  if (BINARY_EXTENSIONS.has(ext)) return 'binary';
  const bytes = readFileSync(path);
  return bytes.includes(0) ? 'binary' : 'text';
}

export function buildTreeManifest(root) {
  const files = listFiles(root).map((path) => {
    const kind = classifyFile(path);
    return {
      path: normalizedRelative(root, path),
      size: statSync(path).size,
      sha256: sha256File(path),
      kind,
    };
  });
  const canonical = files.map((file) => `${file.path}\0${file.size}\0${file.sha256}\n`).join('');
  const countsByClass = files.reduce((counts, file) => {
    counts[file.kind] = (counts[file.kind] ?? 0) + 1;
    return counts;
  }, { text: 0, image: 0, binary: 0 });
  return {
    files,
    fileCount: files.length,
    countsByClass,
    digest: sha256(canonical),
  };
}

export function compareTreeManifests(first, second) {
  const differences = [];
  const left = new Map(first.files.map((file) => [file.path, file]));
  const right = new Map(second.files.map((file) => [file.path, file]));
  for (const path of [...new Set([...left.keys(), ...right.keys()])].sort()) {
    const a = left.get(path);
    const b = right.get(path);
    if (!a) differences.push({ path, difference: 'only-second-generation' });
    else if (!b) differences.push({ path, difference: 'only-first-generation' });
    else if (a.sha256 !== b.sha256 || a.size !== b.size) differences.push({ path, difference: 'content' });
  }
  return differences;
}

function entropyBitsPerCharacter(value) {
  const counts = new Map();
  for (const character of value) counts.set(character, (counts.get(character) ?? 0) + 1);
  let entropy = 0;
  for (const count of counts.values()) {
    const probability = count / value.length;
    entropy -= probability * Math.log2(probability);
  }
  return entropy;
}

function lineNumber(text, index) {
  let line = 1;
  for (let position = 0; position < index; position++) if (text.charCodeAt(position) === 10) line++;
  return line;
}

function collectPatternMatches(text, patterns) {
  const matches = [];
  for (const [category, expression] of patterns) {
    expression.lastIndex = 0;
    for (const match of text.matchAll(expression)) {
      if (category === 'ipv6' && !/[a-f]/iu.test(match[0]) && !match[0].includes('::')) continue;
      matches.push({ category, value: match[0].trim(), index: match.index ?? 0 });
    }
  }
  return matches;
}

function collectHighEntropyMatches(text) {
  const matches = [];
  const expression = /\b[A-Za-z0-9+/_=]{24,}\b/gu;
  for (const match of text.matchAll(expression)) {
    const value = match[0];
    const hasVariety = /[A-Za-z]/.test(value) && /\d/.test(value);
    if (hasVariety && entropyBitsPerCharacter(value) >= 3.5) {
      matches.push({ category: 'high-entropy', value, index: match.index ?? 0 });
    }
  }
  return matches;
}

function approvedValueMap(policy) {
  const map = new Map();
  for (const item of policy.allowedValues ?? []) {
    if (!item.value || !item.category || !item.derivation) {
      throw new Error('Each scrub-policy allowedValues entry needs value, category, and derivation.');
    }
    map.set(item.value, item);
  }
  return map;
}

function sourceTermMatches(text, terms) {
  const lower = text.toLocaleLowerCase('en');
  const matches = [];
  for (const term of terms) {
    let index = lower.indexOf(term.toLocaleLowerCase('en'));
    while (index >= 0) {
      matches.push({ category: 'source-term', value: term, index });
      index = lower.indexOf(term.toLocaleLowerCase('en'), index + term.length);
    }
  }
  return matches;
}

function collectAllowlistedIdentifierMatches(text) {
  const matches = [];
  for (const match of text.matchAll(/\b([A-Z][A-Z0-9]{1,12})-\d+\b/gu)) {
    matches.push({ category: 'task-key', namespace: match[1], value: match[0], index: match.index ?? 0 });
  }
  for (const match of text.matchAll(/\b(?:claude-[a-z0-9.-]*[a-z0-9]|gpt-[a-z0-9.-]*[a-z0-9])\b/giu)) {
    matches.push({ category: 'model-name', value: match[0], index: match.index ?? 0 });
  }
  for (const expression of [
    /"ownerClientId"\s*:\s*"([^"]+)"/giu,
    /"actor"\s*:\s*"human:([^"]+)"/giu,
  ]) {
    for (const match of text.matchAll(expression)) {
      matches.push({ category: 'synthetic-user', value: match[1], index: (match.index ?? 0) + match[0].indexOf(match[1]) });
    }
  }
  return matches;
}

function parsePng(path) {
  const bytes = readFileSync(path);
  const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  if (bytes.length < 8 || !bytes.subarray(0, 8).equals(signature)) throw new Error(`Invalid PNG signature: ${path}`);
  const chunks = [];
  let offset = 8;
  while (offset + 12 <= bytes.length) {
    const length = bytes.readUInt32BE(offset);
    const type = bytes.toString('ascii', offset + 4, offset + 8);
    const dataStart = offset + 8;
    const dataEnd = dataStart + length;
    if (dataEnd + 4 > bytes.length) throw new Error(`Truncated PNG chunk in ${path}`);
    chunks.push({ type, data: bytes.subarray(dataStart, dataEnd) });
    offset = dataEnd + 4;
    if (type === 'IEND') break;
  }
  const header = chunks.find((chunk) => chunk.type === 'IHDR')?.data;
  if (!header || header.length !== 13) throw new Error(`PNG is missing IHDR: ${path}`);
  return {
    width: header.readUInt32BE(0),
    height: header.readUInt32BE(4),
    bitDepth: header[8],
    colorType: header[9],
    interlace: header[12],
    chunks,
  };
}

function builtinRasterOcr(png) {
  if (png.bitDepth !== 8 || png.colorType !== 6 || png.interlace !== 0) {
    return {
      engine: 'agent-studio-raster-run-ocr/v1',
      status: 'unsupported',
      recognizedText: null,
      glyphSizedRunCount: null,
    };
  }
  const raw = inflateSync(Buffer.concat(png.chunks.filter((chunk) => chunk.type === 'IDAT').map((chunk) => chunk.data)));
  const rowSize = png.width * 4 + 1;
  if (raw.length !== rowSize * png.height) throw new Error('PNG raster length does not match IHDR dimensions.');
  let glyphSizedRunCount = 0;
  for (let y = 0; y < png.height; y++) {
    const rowOffset = y * rowSize;
    if (raw[rowOffset] !== 0) {
      return {
        engine: 'agent-studio-raster-run-ocr/v1',
        status: 'unsupported-filter',
        recognizedText: null,
        glyphSizedRunCount: null,
      };
    }
    let runStart = 0;
    for (let x = 1; x <= png.width; x++) {
      const previous = rowOffset + 1 + (x - 1) * 4;
      const current = rowOffset + 1 + x * 4;
      const changed = x === png.width || !raw.subarray(previous, previous + 4).equals(raw.subarray(current, current + 4));
      if (changed) {
        const length = x - runStart;
        if (length >= 1 && length <= 16) glyphSizedRunCount++;
        runStart = x;
      }
    }
  }
  return {
    engine: 'agent-studio-raster-run-ocr/v1',
    status: glyphSizedRunCount <= 128 ? 'no-text-detected' : 'glyph-candidates-detected',
    recognizedText: '',
    glyphSizedRunCount,
    candidateThreshold: 128,
  };
}

function inspectImage(root, path) {
  const png = parsePng(path);
  const metadataChunkTypes = new Set(['tEXt', 'zTXt', 'iTXt', 'eXIf', 'tIME', 'iCCP', 'sPLT']);
  const metadataChunks = png.chunks.filter((chunk) => metadataChunkTypes.has(chunk.type)).map((chunk) => chunk.type);
  return {
    path: normalizedRelative(root, path),
    sha256: sha256File(path),
    format: 'png',
    width: png.width,
    height: png.height,
    metadata: {
      status: metadataChunks.length === 0 ? 'clean' : 'metadata-present',
      chunks: metadataChunks,
    },
    ocr: builtinRasterOcr(png),
  };
}

export function loadSourceTerms(path) {
  if (!path || !existsSync(path)) throw new Error('A private --source-terms-file is required for source-name scanning.');
  const terms = readFileSync(path, 'utf8')
    .split(/\r?\n/u)
    .map((value) => value.trim())
    .filter((value) => value && !value.startsWith('#'));
  if (terms.length === 0) throw new Error('The private source-terms file must contain at least one term.');
  return terms;
}

export function scanGeneratedDatastore({ root, manifest, policy, sourceTerms }) {
  const approved = approvedValueMap(policy);
  const violations = [];
  const allowedMatchCounts = new Map();
  const images = [];
  for (const file of manifest.files) {
    const path = resolve(root, file.path);
    if (file.kind === 'image') {
      const image = inspectImage(root, path);
      images.push(image);
      if (image.metadata.status !== 'clean') violations.push({ category: 'image-metadata', path: file.path, line: null, fingerprint: sha256(file.path).slice(0, 16) });
      if (image.ocr.status !== 'no-text-detected') violations.push({ category: 'image-ocr', path: file.path, line: null, fingerprint: sha256(file.path).slice(0, 16) });
      continue;
    }
    if (file.kind !== 'text') continue;
    const text = readFileSync(path, 'utf8');
    const matches = [
      ...collectPatternMatches(text, SECRET_PATTERNS),
      ...collectPatternMatches(text, DISCLOSURE_PATTERNS),
      ...collectHighEntropyMatches(text),
      ...sourceTermMatches(text, sourceTerms),
      ...collectAllowlistedIdentifierMatches(text),
    ];
    const dedupe = new Set();
    for (const match of matches) {
      const key = `${match.category}\0${match.index}\0${match.value}`;
      if (dedupe.has(key)) continue;
      dedupe.add(key);
      if (match.category === 'task-key') {
        const taskNamespaces = policy.allowedTaskKeyNamespaces ?? [];
        const referenceNamespaces = policy.allowedReferenceNamespaces ?? [];
        if (taskNamespaces.includes(match.namespace) || referenceNamespaces.includes(match.namespace)) {
          const allowKey = `${match.category}\0Namespace ${match.namespace} is an explicit synthetic or public-document reference namespace.`;
          allowedMatchCounts.set(allowKey, (allowedMatchCounts.get(allowKey) ?? 0) + 1);
          continue;
        }
      }
      if (match.category === 'model-name' && (policy.allowedSyntheticModels ?? []).includes(match.value)) {
        const allowKey = `${match.category}\0Synthetic model name is enumerated by the committed scrub policy.`;
        allowedMatchCounts.set(allowKey, (allowedMatchCounts.get(allowKey) ?? 0) + 1);
        continue;
      }
      if (match.category === 'synthetic-user' && (policy.allowedSyntheticUsers ?? []).includes(match.value)) {
        const allowKey = `${match.category}\0Synthetic user is enumerated by the committed scrub policy.`;
        allowedMatchCounts.set(allowKey, (allowedMatchCounts.get(allowKey) ?? 0) + 1);
        continue;
      }
      const allow = approved.get(match.value);
      if (allow && match.category !== 'source-term' && (allow.matchCategories ?? []).includes(match.category)) {
        const allowKey = `${allow.category}\0${allow.derivation}`;
        allowedMatchCounts.set(allowKey, (allowedMatchCounts.get(allowKey) ?? 0) + 1);
        continue;
      }
      violations.push({
        category: match.category,
        path: file.path,
        line: lineNumber(text, match.index),
        fingerprint: sha256(match.value).slice(0, 16),
      });
    }
  }
  return {
    status: violations.length === 0 ? 'passed' : 'failed',
    scannedFileCount: manifest.fileCount,
    violations,
    allowedMatches: [...allowedMatchCounts.entries()].map(([key, count]) => {
      const [category, derivation] = key.split('\0');
      return { category, count, derivation };
    }).sort((a, b) => a.category.localeCompare(b.category, 'en')),
    images,
  };
}

export function validateReleaseVersion(value) {
  if (!/^\d{4}\.\d{2}\.\d+$/u.test(value ?? '')) {
    throw new Error('Demo release must use YYYY.MM.N, for example 2026.08.1.');
  }
  return value;
}

export function stableJson(value) {
  return `${JSON.stringify(value, null, 2)}\n`;
}

export function assertSafeArchiveEntry(value) {
  if (!value || value.startsWith('/') || value.split('/').includes('..') || value.includes('\\')) {
    throw new Error(`Unsafe bundle archive entry: ${value}`);
  }
}

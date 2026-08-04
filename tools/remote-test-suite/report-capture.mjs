import {
  cp,
  mkdir,
  readFile,
  readdir,
  writeFile
} from 'node:fs/promises';
import path from 'node:path';

export const redactedEnvironmentValue = '[REDACTED]';

export function isSensitiveEnvironmentName(name) {
  return /TOKEN|SECRET|KEY|(?:^|_)PAT(?:_|$)/i.test(String(name));
}

export function sanitizeEnvironmentValues(value, secrets = []) {
  let sanitized = String(value);
  for (const secret of secrets.filter(Boolean)) {
    sanitized = sanitized.split(secret).join(redactedEnvironmentValue);
  }

  try {
    const parsed = JSON.parse(sanitized);
    sanitizeEnvironmentArrays(parsed);
    return `${JSON.stringify(parsed, null, 2)}\n`;
  } catch {
    return sanitizeEnvironmentAssignments(sanitized);
  }
}

export async function copyReportEvidenceTree(source, destination, relative = '') {
  await mkdir(destination, { recursive: true });
  const entries = await readdir(source, { withFileTypes: true });
  for (const entry of entries) {
    const sourcePath = path.join(source, entry.name);
    const destinationPath = path.join(destination, entry.name);
    const relativePath = path.join(relative, entry.name);
    if (entry.isDirectory()) {
      await copyReportEvidenceTree(sourcePath, destinationPath, relativePath);
      continue;
    }
    if (!entry.isFile()) {
      throw new Error(`Report evidence contains an unsupported entry: ${relativePath}`);
    }
    if (isTextEvidence(entry.name)) {
      const contents = await readFile(sourcePath, 'utf8');
      await writeFile(destinationPath, sanitizeEnvironmentValues(contents));
    } else {
      await cp(sourcePath, destinationPath);
    }
  }
}

function sanitizeEnvironmentArrays(value) {
  if (!value || typeof value !== 'object') return;
  if (Array.isArray(value)) {
    for (const item of value) sanitizeEnvironmentArrays(item);
    return;
  }
  for (const [name, child] of Object.entries(value)) {
    if (name.toLowerCase() === 'env' && Array.isArray(child)) {
      value[name] = child.map(item => redactEnvironmentAssignment(item));
    } else {
      sanitizeEnvironmentArrays(child);
    }
  }
}

function redactEnvironmentAssignment(value) {
  if (typeof value !== 'string') return value;
  const separator = value.indexOf('=');
  if (separator < 0) return value;
  const name = value.slice(0, separator);
  return isSensitiveEnvironmentName(name)
    ? `${name}=${redactedEnvironmentValue}`
    : value;
}

function sanitizeEnvironmentAssignments(value) {
  return value.replace(
    /(^|[\s"'[,])([A-Za-z_][A-Za-z0-9_]*)=([^\r\n"',\]]*)/gm,
    (match, prefix, name) => isSensitiveEnvironmentName(name)
      ? `${prefix}${name}=${redactedEnvironmentValue}`
      : match);
}

function isTextEvidence(name) {
  return /\.(?:json|jsonl|log|md|txt|ya?ml)$/i.test(name);
}

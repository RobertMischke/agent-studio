import { readFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const manifestPath = path.join(repoRoot, 'docs', 'visual', 'manifest.json');
const manifestDir = path.dirname(manifestPath);

const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
const failures = [];

function requireField(owner, field, label) {
  if (!owner[field]) failures.push(`${label} is missing ${field}`);
}

if (!Array.isArray(manifest.images) || manifest.images.length === 0) {
  failures.push('manifest.images must contain at least one image');
}

for (const image of manifest.images ?? []) {
  const label = `image ${image.id ?? '<missing id>'}`;
  requireField(image, 'id', label);
  requireField(image, 'featureDoc', label);
  requireField(image, 'purpose', label);
  requireField(image, 'state', label);
  requireField(image, 'capture', label);

  const featureDoc = image.featureDoc
    ? path.join(manifestDir, image.featureDoc)
    : null;
  if (featureDoc && !existsSync(featureDoc)) {
    failures.push(`${label} feature doc does not exist: ${image.featureDoc}`);
  }

  const imagePath = image.image?.path
    ? path.join(manifestDir, image.image.path)
    : null;
  if (!imagePath) {
    failures.push(`${label} image.path is missing`);
  } else if (!existsSync(imagePath)) {
    failures.push(`${label} image does not exist: ${image.image.path}`);
  }

  requireField(image.image ?? {}, 'alt', `${label}.image`);
  requireField(image.image ?? {}, 'caption', `${label}.image`);
  requireField(image.state ?? {}, 'route', `${label}.state`);
  requireField(image.state ?? {}, 'viewport', `${label}.state`);
  requireField(image.state ?? {}, 'relevantState', `${label}.state`);
  requireField(image.state ?? {}, 'dataSource', `${label}.state`);
  requireField(image.capture ?? {}, 'test', `${label}.capture`);

  if (!Array.isArray(image.capture?.steps) || image.capture.steps.length === 0) {
    failures.push(`${label}.capture.steps must contain at least one step`);
  }

  if (!Array.isArray(image.marketingUsages) || image.marketingUsages.length === 0) {
    failures.push(`${label} must declare at least one marketing usage`);
  }

  for (const usage of image.marketingUsages ?? []) {
    const usageLabel = `${label} usage ${usage.id ?? '<missing id>'}`;
    for (const field of ['id', 'repo', 'assetPath', 'publicPath', 'component', 'role', 'copyIntent']) {
      requireField(usage, field, usageLabel);
    }
    if (!Array.isArray(usage.pages) || usage.pages.length === 0) {
      failures.push(`${usageLabel} must declare at least one page`);
    }
  }

  if (featureDoc && image.image?.path && existsSync(featureDoc)) {
    const doc = await readFile(featureDoc, 'utf8');
    const imageName = path.basename(image.image.path);
    if (!doc.includes(imageName)) {
      failures.push(`${label} feature doc does not embed ${imageName}`);
    }
    if (!doc.includes(image.id)) {
      failures.push(`${label} feature doc does not mention manifest id ${image.id}`);
    }
  }
}

if (failures.length) {
  console.error(`Visual documentation validation failed (${failures.length}):`);
  for (const failure of failures) console.error(`- ${failure}`);
  process.exit(1);
}

console.log(`Visual documentation validation passed (${manifest.images.length} images).`);

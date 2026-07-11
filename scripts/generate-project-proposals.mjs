#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';

const args = Object.fromEntries(process.argv.slice(2).map(value => {
  const [key, ...rest] = value.replace(/^--/, '').split('=');
  return [key, rest.join('=')];
}));
const input = args.input;
const output = args.output ?? 'docs/proposals';
const generation = args.generation ?? new Date().toISOString().slice(0, 10);
const severities = new Set((args.severities ?? 'critical,medium').split(',').map(v => v.trim().toLowerCase()));
const limits = Object.fromEntries((args.limits ?? '').split(',').filter(Boolean).map(part => {
  const [severity, limit] = part.split(':');
  return [severity.trim().toLowerCase(), Number(limit)];
}));
if (!input) throw new Error('Usage: --input=<survey.html> [--output=docs/proposals] [--generation=YYYY-MM-DD]');

const html = fs.readFileSync(input, 'utf8');
const articles = [...html.matchAll(/<article class="shot"[\s\S]*?<\/article>/g)].map(match => match[0]);
const generationDir = path.resolve(output, generation);
const assetDir = path.join(generationDir, 'assets');
fs.mkdirSync(assetDir, { recursive: true });
const decode = value => value.replace(/<[^>]+>/g, '').replaceAll('&amp;', '&').replaceAll('&quot;', '"')
  .replaceAll('&#39;', "'").replaceAll('&lt;', '<').replaceAll('&gt;', '>').replaceAll('·', '-').trim();
const field = (body, regex) => decode(body.match(regex)?.[1] ?? '');
const quote = value => JSON.stringify(value);
const slug = value => value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 72);

let created = 0;
let preserved = 0;
const counts = {};
for (const article of articles) {
  const severity = article.match(/data-severity="([^"]+)"/)?.[1]?.toLowerCase();
  if (!severity || !severities.has(severity)) continue;
  if (Number.isFinite(limits[severity]) && (counts[severity] ?? 0) >= limits[severity]) continue;
  counts[severity] = (counts[severity] ?? 0) + 1;
  const number = field(article, /<span class="number">([\s\S]*?)<\/span>/) || String(created + preserved + 1).padStart(3, '0');
  const title = field(article, /<h2>([\s\S]*?)<\/h2>/);
  const finding = field(article, /<p class="summary">([\s\S]*?)<\/p>/);
  const proposal = field(article, /<dt>Improve<\/dt><dd>([\s\S]*?)<\/dd>/) || `Address the measured issue: ${title}.`;
  const fileName = field(article, /<p class="filename">([\s\S]*?)<\/p>/);
  const image = article.match(/src="data:image\/png;base64,([^"]+)"/)?.[1];
  const id = `survey-${generation}-${number}-${slug(title)}`;
  const relImage = `assets/${number}-${slug(title)}.png`;
  const docPath = path.join(generationDir, `${id}.md`);
  if (image) fs.writeFileSync(path.join(generationDir, relImage), Buffer.from(image, 'base64'));
  if (fs.existsSync(docPath)) { preserved++; continue; }
  const effort = proposal.length > 240 ? 'large' : proposal.length > 130 ? 'medium' : 'small';
  const document = `---\n` +
    `id: ${quote(id)}\ngeneration: ${quote(generation)}\nfinding: ${quote(finding)}\n` +
    `evidenceScreenshot: ${quote(`${generation}/${relImage}`)}\nproposal: ${quote(proposal)}\n` +
    `estimatedEffort: ${quote(effort)}\nseverity: ${quote(severity)}\nstatus: "proposed"\nspawnedTask: null\n---\n\n` +
    `# ${proposal}\n\n## Finding\n\n${finding}\n\n## Evidence\n\n![${title}](./${relImage})\n\n` +
    `Source capture: \`${fileName}\`\n\n## Proposal\n\n${proposal}\n\nEstimated effort: **${effort}**  \nSeverity: **${severity}**\n`;
  fs.writeFileSync(docPath, document);
  created++;
}
const manifestPath = path.join(generationDir, 'generation.json');
const total = Object.values(counts).reduce((sum, count) => sum + count, 0);
fs.writeFileSync(manifestPath, JSON.stringify({ generation, source: path.resolve(input), severities: [...severities], limits, total, createdThisRun: created, preserved, counts }, null, 2) + '\n');
console.log(JSON.stringify({ generation, total, createdThisRun: created, preserved, counts, manifestPath }, null, 2));

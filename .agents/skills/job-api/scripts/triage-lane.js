// Template: bulk-classify + move tasks from one lane.
//
// Pattern from the 2026-05-11 human-review triage of 108 tasks. Reads each
// task's status.md and aspect-*.md, classifies into action buckets, then
// loops the move API per task. Idempotent prompt-append so re-running the
// reissue path does not stack duplicate notes.
//
// Adapt the classify() function to your specific lane. The boilerplate
// below filters out the "bogus aspect-runner produced no parseable verdict"
// false-positive class that was common before the 2026-05-11 aspect-runner
// fix.

const fs = require('fs');
const path = require('path');
const http = require('http');

const HOST = '127.0.0.1';
const PORT = 5031;
const lane = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard\\5-human-review';
const watchPath = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

const BOGUS_ASPECT = /Aspect runner produced no parseable verdict/i;

function classify() {
  const out = { DONE: [], OPEN: [], CHECK: [] };
  const folders = fs.readdirSync(lane).filter(d => fs.statSync(path.join(lane, d)).isDirectory());
  for (const slug of folders) {
    const folder = path.join(lane, slug);
    let realConcerns = 0, bogusConcerns = 0;
    let hasOpen = false, hasDone = false;
    let openItemsText = '';
    const statusPath = path.join(folder, 'status.md');
    if (fs.existsSync(statusPath)) {
      const text = fs.readFileSync(statusPath, 'utf-8');
      const m = text.match(/##\s*open\s+items\s*\n([\s\S]*?)(?=\n##|\n$|$)/i);
      if (m) {
        const bullets = m[1].split('\n')
          .filter(l => l.trim().startsWith('-') && l.trim().length > 5)
          .filter(l => !/^-\s*(none|none\.|\(none\))$/i.test(l.trim()));
        if (bullets.length) {
          hasOpen = true;
          openItemsText = bullets.slice(0, 8).join('\n');
        }
      }
      if (/result:\s*(complete|done|success)/i.test(text)) hasDone = true;
    }
    for (const f of fs.readdirSync(folder).filter(f => f.startsWith('aspect-') && f.endsWith('.md'))) {
      const t = fs.readFileSync(path.join(folder, f), 'utf-8');
      const sm = t.match(/^status:\s*(\w+)/mi);
      const summary = (t.match(/^summary:\s*(.+)$/mi) || [, ''])[1];
      if (sm && sm[1].toLowerCase() === 'concerns') {
        if (BOGUS_ASPECT.test(summary)) bogusConcerns++;
        else realConcerns++;
      }
    }
    if (hasOpen) out.OPEN.push({ slug, openItemsText });
    else if (hasDone || bogusConcerns > 0) out.DONE.push({ slug });
    else out.CHECK.push({ slug });
  }
  return out;
}

function moveTo(slug, targetState) {
  return new Promise(resolve => {
    const body = JSON.stringify({ targetState });
    const reqPath = `/api/jobs/${encodeURIComponent(slug)}/state?watchPath=${encodeURIComponent(watchPath)}`;
    const req = http.request({
      hostname: HOST, port: PORT, path: reqPath, method: 'PUT',
      headers: {
        'Content-Type': 'application/json',
        'X-Client-Id': 'local-default',
        'Content-Length': Buffer.byteLength(body),
      },
    }, res => { let d = ''; res.on('data', c => d += c); res.on('end', () => resolve({ slug, status: res.statusCode, body: d.slice(0, 200) })); });
    req.on('error', e => resolve({ slug, status: -1, body: e.message }));
    req.write(body); req.end();
  });
}

function appendReissueNote(slug, openItemsText) {
  const promptPath = path.join(lane, slug, 'prompt.md');
  if (!fs.existsSync(promptPath)) return false;
  const original = fs.readFileSync(promptPath, 'utf-8');
  if (original.includes('Human Review Reissue Note')) return false; // idempotent
  const today = new Date().toISOString().slice(0, 10);
  const note = [
    '',
    '---',
    '',
    `## Human Review Reissue Note (${today})`,
    '',
    'Triagiert in `5-human-review`. Der vorherige Run hat folgende Open Items dokumentiert:',
    '',
    openItemsText || '(no specific open items found, please re-evaluate)',
    '',
    'Bitte diese Punkte adressieren oder dokumentieren warum sie out-of-scope sind.',
    '',
  ].join('\n');
  fs.writeFileSync(promptPath, original + note, 'utf-8');
  return true;
}

async function main() {
  const buckets = classify();
  console.log('Buckets:');
  for (const k of Object.keys(buckets)) console.log(`  ${k}: ${buckets[k].length}`);

  const arg = process.argv[2];
  if (arg === '--move-done') {
    let ok = 0, fail = 0;
    for (const { slug } of buckets.DONE) {
      const r = await moveTo(slug, '6-completed');
      r.status === 200 ? ok++ : fail++;
    }
    console.log(`DONE → 6-completed: ok=${ok} fail=${fail}`);
  } else if (arg === '--reissue-open') {
    let ok = 0, fail = 0;
    for (const { slug, openItemsText } of buckets.OPEN) {
      appendReissueNote(slug, openItemsText);
      const r = await moveTo(slug, '2-ready');
      r.status === 200 ? ok++ : fail++;
    }
    console.log(`OPEN → 2-ready (with note): ok=${ok} fail=${fail}`);
  } else {
    console.log('Usage: --move-done | --reissue-open');
    console.log('Run without flags to just print bucket counts (dry-run).');
    console.log('\nCHECK bucket (manual triage):');
    for (const { slug } of buckets.CHECK) console.log('  ' + slug);
  }
}

main().catch(e => { console.error('failed:', e); process.exit(1); });

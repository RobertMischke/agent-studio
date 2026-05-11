// Template: create one job.
//
// Usage:
//   1. Adjust HOST / PORT below (5031=stable, 5030=dev).
//   2. Replace the `watchPath` with the `path` field from
//      `GET /api/watch-paths` for your target project.
//   3. Fill `id`, `title`, `targetState`, `taskType`, and `promptMarkdown`.
//   4. Run `node create-job.js`.
//
// Returns 200 with `{ id }` on success; 409 "Job already exists or invalid
// input" usually means the watchPath does not match GET /api/watch-paths
// (use the `path` field, not `rootPath`).

const http = require('http');

const HOST = '127.0.0.1';
const PORT = 5031; // stable; use 5030 for dev

const watchPath = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

const job = {
  id: 'REPLACE-ME-stable-slug',
  title: 'REPLACE ME - Human-readable title shown on the card',
  targetState: '2-ready',     // initial lane; see references/states.md
  order: 0,                    // position within lane; 0 = top of new batch
  taskType: 'bug',             // bug | feature | refactor | analysis | chore | cleanup
  agent: 'claude',             // claude | codex | copilot | gemini
  cliType: 'claude',           // usually matches agent
  watchPath,
  promptMarkdown: [
    '## Context',
    '',
    'Replace this body with the full task prompt as Markdown.',
    '',
    '## Acceptance Criteria',
    '',
    '1. ...',
  ].join('\n'),
};

const body = JSON.stringify(job);
const req = http.request({
  hostname: HOST, port: PORT, path: '/api/jobs', method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    'X-Client-Id': 'local-default',
    'Content-Length': Buffer.byteLength(body),
  },
}, res => {
  let d = '';
  res.on('data', c => d += c);
  res.on('end', () => console.log('status:', res.statusCode, '| body:', d.slice(0, 400) || '(empty)'));
});
req.on('error', e => console.log('ERR', e.message));
req.write(body);
req.end();

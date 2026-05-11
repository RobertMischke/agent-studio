// Template: promote a job to the head of its 2-ready queue.
//
// Usage:
//   node move-to-top.js <jobId>
//
// Returns { position: 0 } on success. 404 if the job is not found in
// 2-ready (or in 0-backlog with the promotion path enabled). Use this when
// a hot bug needs to be picked up before the existing queue.

const http = require('http');

const HOST = '127.0.0.1';
const PORT = 5031;
const watchPath = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

const jobId = process.argv[2];
if (!jobId) {
  console.error('usage: node move-to-top.js <jobId>');
  process.exit(1);
}

const path = `/api/jobs/${encodeURIComponent(jobId)}/move-to-top` +
             `?watchPath=${encodeURIComponent(watchPath)}`;

const req = http.request({
  hostname: HOST, port: PORT, path, method: 'POST',
  headers: {
    'X-Client-Id': 'local-default',
    'Content-Length': 0,
  },
}, res => {
  let d = '';
  res.on('data', c => d += c);
  res.on('end', () => console.log('status:', res.statusCode, '| body:', d.slice(0, 200) || '(empty)'));
});
req.on('error', e => console.log('ERR', e.message));
req.end();

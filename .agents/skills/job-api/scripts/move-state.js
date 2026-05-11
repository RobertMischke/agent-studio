// Template: move one job between lanes.
//
// Usage:
//   node move-state.js <jobId> <targetState>
//
// Example:
//   node move-state.js fix-card-delete-bug 6-completed
//
// Adjust HOST / PORT and `watchPath` below for your target project.

const http = require('http');

const HOST = '127.0.0.1';
const PORT = 5031;
const watchPath = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

const jobId = process.argv[2];
const targetState = process.argv[3];

if (!jobId || !targetState) {
  console.error('usage: node move-state.js <jobId> <targetState>');
  console.error('       targetState: 0-backlog | 1-preparation | 1b-needs-human-review |');
  console.error('                    2-ready | 6-completed | 7-archive | ...');
  process.exit(1);
}

const body = JSON.stringify({ targetState });
const path = `/api/jobs/${encodeURIComponent(jobId)}/state` +
             `?watchPath=${encodeURIComponent(watchPath)}`;

const req = http.request({
  hostname: HOST, port: PORT, path, method: 'PUT',
  headers: {
    'Content-Type': 'application/json',
    'X-Client-Id': 'local-default',
    'Content-Length': Buffer.byteLength(body),
  },
}, res => {
  let d = '';
  res.on('data', c => d += c);
  res.on('end', () => console.log('status:', res.statusCode, '| body:', d.slice(0, 200) || '(empty)'));
});
req.on('error', e => console.log('ERR', e.message));
req.write(body);
req.end();

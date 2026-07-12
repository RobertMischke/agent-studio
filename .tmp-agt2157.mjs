import http from 'node:http';
const get = path => new Promise((resolve, reject) => {
  const request = http.get({ hostname: '127.0.0.1', port: 5031, path,
    headers: { 'X-Client-Id': 'local-default' } }, response => {
    let body = '';
    response.on('data', chunk => body += chunk);
    response.on('end', () => resolve({ status: response.statusCode, headers: response.headers, body }));
  });
  request.on('error', reject);
});
const watchPaths = JSON.parse((await get('/api/watch-paths')).body);
const watchPath = watchPaths.find(item => item.name === 'Agent Studio').path;
const response = await get('/api/tasks/agt-2157-project-hub-pipeline-prompt-model-config?watchPath=' + encodeURIComponent(watchPath));
console.log(JSON.stringify({ status: response.status, headers: response.headers, bytes: response.body.length,
  prefix: response.body.slice(0, 2000) }, null, 2));

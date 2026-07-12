const base = 'http://127.0.0.1:5031';
const headers = { 'X-Client-Id': 'local-default', 'Content-Type': 'application/json' };
const watchPaths = await fetch(base + '/api/watch-paths', { headers }).then(r => r.json());
const byProject = new Map(watchPaths.map(x => [x.name, x.path]));
const targets = [
  ['Coding Agent Chat', 'versioned-package-release-and-provenance-manifest'],
  ['Coding Agent Chat', 'agt-2149-replay-structured-warnings-session-turn-metrics-and-stable-scroll'],
  ['Coding Agent Runner', 'typed-cli-diagnostics-for-warnings-session-and-turn-metadata'],
];
for (const [project, id] of targets) {
  const path = `/api/tasks/${encodeURIComponent(id)}/stop?watchPath=${encodeURIComponent(byProject.get(project))}`;
  const response = await fetch(base + path, { method: 'POST', headers, signal: AbortSignal.timeout(15000) });
  console.log(`stop\t${response.status}\t${project}\t${id}`);
}

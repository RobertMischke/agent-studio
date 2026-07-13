const base = 'http://localhost:5031';
const project = 'Agent Studio';

const supportSteps = [
  'pre-orchestrator-prep',
  'aspect-requirement-fit',
  'aspect-code-quality',
  'aspect-documentation-impact',
  'aspect-tests-and-evidence',
  'post-conflict-resolution',
  'post-workstream-collector',
  'post-orchestrator-decision',
  'post-drift-adr-code',
  'post-drift-software-architecture',
  'post-drift-docs-marketing',
  'post-drift-spec-task-job',
  'post-drift-code-pattern',
  'post-abort-review',
];

const qualitySteps = [
  'post-code-review-grade',
  'post-task-spawner',
];

async function update(stepId, model, thinkingLevel) {
  const response = await fetch(`${base}/api/projects/${encodeURIComponent(project)}/pipeline-step`, {
    method: 'PUT',
    headers: {
      'content-type': 'application/json',
      'x-client-id': 'local-default',
    },
    body: JSON.stringify({ stepId, cliType: 'codex', model, thinkingLevel }),
    signal: AbortSignal.timeout(120_000),
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${stepId}: HTTP ${response.status}: ${text}`);
  return `${stepId}: ${response.status}`;
}

const results = await Promise.all([
  ...supportSteps.map(stepId => update(stepId, 'gpt-5.4-mini', 'high')),
  ...qualitySteps.map(stepId => update(stepId, 'gpt-5.6-sol', 'ultra')),
]);
for (const result of results) console.log(result);

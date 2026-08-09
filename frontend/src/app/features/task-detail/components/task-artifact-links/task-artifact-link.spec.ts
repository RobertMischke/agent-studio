import { describe, expect, it } from 'vitest';
import { resolveTaskArtifactLink } from './task-artifact-link';

const CONTEXT = {
  jobId: 'task with spaces',
  watchPath: 'C:/Projects/Agent Studio',
};

describe('resolveTaskArtifactLink', () => {
  it('binds a relative HTML result to the current task artifact route', () => {
    expect(resolveTaskArtifactLink('results/report.html', CONTEXT)).toEqual({
      relativePath: 'results/report.html',
      href: '/api/tasks/task%20with%20spaces/results/report.html?watchPath=C%3A%2FProjects%2FAgent%20Studio',
      html: true,
    });
  });

  it('drops an absolute runner prefix and keeps the task-relative result suffix', () => {
    expect(resolveTaskArtifactLink(
      '/home/agent/runner-work/tasks/AGT-2514/results/reports/concept report.html#context-model',
      CONTEXT,
    )).toEqual({
      relativePath: 'results/reports/concept report.html',
      href: '/api/tasks/task%20with%20spaces/results/reports/concept%20report.html?watchPath=C%3A%2FProjects%2FAgent%20Studio#context-model',
      html: true,
    });
  });

  it('binds Windows runner paths to the current card rather than the runner host', () => {
    expect(resolveTaskArtifactLink(
      'C:\\runner\\tasks\\AGT-2514\\results\\report.pdf',
      CONTEXT,
    )?.href).toBe(
      '/api/tasks/task%20with%20spaces/results/report.pdf?watchPath=C%3A%2FProjects%2FAgent%20Studio',
    );
  });

  it('serves allowed task logs through the workspace-scoped file route', () => {
    expect(resolveTaskArtifactLink('logs/run-context/run 1.md', CONTEXT)).toEqual({
      relativePath: 'logs/run-context/run 1.md',
      href: '/api/tasks/task%20with%20spaces/files/logs/run-context/run%201.md?watchPath=C%3A%2FProjects%2FAgent%20Studio&scope=workspace',
      html: false,
    });
  });

  it.each([
    '../results/report.html',
    'results/../secret.txt',
    'results/%2e%2e/secret.txt',
    'https://example.test/results/report.html',
    '/api/tasks/other/results/report.html',
    'docs/results/example.md',
    'logs/report.html',
    'logs/archive.bin',
  ])('does not rewrite unsafe or unrelated href %s', (href) => {
    expect(resolveTaskArtifactLink(href, CONTEXT)).toBeNull();
  });
});

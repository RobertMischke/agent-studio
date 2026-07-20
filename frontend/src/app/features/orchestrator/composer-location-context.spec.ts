import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../models/task.model';
import { buildComposerLocationContext } from './composer-location-context';

const task = {
  taskKey: 'C:/tasks/AGT-2162',
  displayKey: 'AGT-2162',
  projectName: 'Agent Studio',
} as TaskInfo;

describe('buildComposerLocationContext', () => {
  it('maps a project Board from the canonical active tab', () => {
    expect(buildComposerLocationContext(
      { kind: 'board', projectName: 'Agent Studio' },
      [task],
    )).toEqual({ project: 'Agent Studio', surface: 'Board' });
  });

  it('maps a task tab with project and task identity', () => {
    expect(buildComposerLocationContext(
      { kind: 'task', taskKey: task.taskKey },
      [task],
    )).toEqual({ project: 'Agent Studio', surface: 'Task', detail: 'AGT-2162' });
  });

  it.each([
    [{ kind: 'hub', projectName: 'Agent Studio' } as const, 'Project Hub'],
    [{ kind: 'hub', projectName: 'Agent Studio', section: 'overview' } as const, 'Overview'],
    [{ kind: 'hub', projectName: 'Agent Studio', section: 'wiki' } as const, 'Wiki'],
    [{ kind: 'hub', projectName: 'Agent Studio', section: 'project-urls' } as const, 'Project URLs'],
    [{ kind: 'hub', projectName: 'Agent Studio', section: 'drift' } as const, 'Drift'],
  ])('maps project-level tab %o to %s', (tab, surface) => {
    expect(buildComposerLocationContext(tab, [task]))
      .toEqual({ project: 'Agent Studio', surface });
  });

  it('maps a URL preview with project and URL identity', () => {
    expect(buildComposerLocationContext(
      { kind: 'url-preview', projectName: 'Agent Studio', urlId: 'studio-dev' },
      [task],
    )).toEqual({ project: 'Agent Studio', surface: 'URL preview', detail: 'studio-dev' });
  });
});

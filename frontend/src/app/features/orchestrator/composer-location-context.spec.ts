<<<<<<< HEAD
=======
import '@angular/compiler';
>>>>>>> origin/task/agt-2163-orchestrator-full-model-picker
import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../models/task.model';
import { buildComposerLocationContext } from './composer-location-context';

const task = {
<<<<<<< HEAD
  taskKey: 'C:/tasks/AGT-2162',
  displayKey: 'AGT-2162',
=======
  taskKey: 'C:/tasks/AGT-2163',
  displayKey: 'AGT-2163',
>>>>>>> origin/task/agt-2163-orchestrator-full-model-picker
  projectName: 'Agent Studio',
} as TaskInfo;

describe('buildComposerLocationContext', () => {
<<<<<<< HEAD
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
=======
  it('maps Board, Task, Project Hub, and URL preview from canonical tabs', () => {
    expect(buildComposerLocationContext(
      { kind: 'board', projectName: 'Agent Studio' }, [task],
    )).toEqual({ project: 'Agent Studio', surface: 'Board' });
    expect(buildComposerLocationContext(
      { kind: 'task', taskKey: task.taskKey }, [task],
    )).toEqual({ project: 'Agent Studio', surface: 'Task', detail: 'AGT-2163' });
    expect(buildComposerLocationContext(
      { kind: 'hub', projectName: 'Agent Studio' }, [task],
    )).toEqual({ project: 'Agent Studio', surface: 'Project Hub' });
    expect(buildComposerLocationContext(
      { kind: 'url-preview', projectName: 'Agent Studio', urlId: 'studio-dev' }, [task],
    )).toEqual({ project: 'Agent Studio', surface: 'URL preview', detail: 'studio-dev' });
  });

  it('uses the canonical project rail label for hub sections', () => {
    expect(buildComposerLocationContext(
      { kind: 'hub', projectName: 'Agent Studio', section: 'wiki' }, [task],
    )).toEqual({ project: 'Agent Studio', surface: 'Wiki' });
  });
>>>>>>> origin/task/agt-2163-orchestrator-full-model-picker
});

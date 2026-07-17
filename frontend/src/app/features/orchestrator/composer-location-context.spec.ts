import '@angular/compiler';
import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../models/task.model';
import { buildComposerLocationContext } from './composer-location-context';

const task = {
  taskKey: 'C:/tasks/AGT-2163',
  displayKey: 'AGT-2163',
  projectName: 'Agent Studio',
} as TaskInfo;

describe('buildComposerLocationContext', () => {
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
});

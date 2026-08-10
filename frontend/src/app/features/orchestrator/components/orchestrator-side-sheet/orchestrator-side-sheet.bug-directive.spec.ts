import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { ChatEvent } from 'coding-agent-chat/core';
import type { TaskService } from '../../../../services/task.service';
import { handleBugDirective } from './orchestrator-side-sheet.bug-directive';

describe('orchestrator side-sheet /bug directive', () => {
  it('still creates a backlog bug after composer attachments are removed', () => {
    const createJob = vi.fn().mockReturnValue(of({ id: 'bug-1' }));
    const refresh = vi.fn();
    const users: string[] = [];
    const events: ChatEvent[] = [];
    const targets: { eventId: string; jobId: string; watchPath: string }[] = [];

    handleBugDirective({
      text: '/bug Context chips overlap\n#frontend',
      project: 'demo-project',
      watchPaths: [{
        name: 'demo-project',
        path: '/tmp/demo-project',
        rootPath: '/tmp/demo-project',
      }],
      jobService: { createJob, refresh } as unknown as TaskService,
      appendUser: (_id, _timestamp, text) => users.push(text),
      appendEvent: event => events.push(event),
      addTarget: (eventId, jobId, watchPath) => targets.push({ eventId, jobId, watchPath }),
    });

    expect(createJob).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Context chips overlap',
      targetState: '0-backlog',
      taskType: 'bug',
      tags: ['frontend'],
      watchPath: '/tmp/demo-project',
    }));
    expect(users).toEqual(['/bug Context chips overlap\n#frontend']);
    expect(events).toHaveLength(1);
    expect(events[0].summary).toContain('Bug filed in 0-backlog');
    expect(targets).toEqual([{
      eventId: 'bug-ok:bug-1',
      jobId: 'bug-1',
      watchPath: '/tmp/demo-project',
    }]);
    expect(refresh).toHaveBeenCalledWith(true);
  });
});

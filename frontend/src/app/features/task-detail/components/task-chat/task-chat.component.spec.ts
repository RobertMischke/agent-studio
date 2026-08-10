import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideCodingAgentChat } from 'coding-agent-chat';
import type { ChatSubmitEvent } from 'coding-agent-chat/core';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { TaskDetail } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { TaskChatComponent } from './task-chat.component';

const DETAIL = {
  info: {
    id: 'agt-2574-folder',
    taskKey: 'AGT-2574',
    title: 'Add Task Chat',
    state: '2-ready',
    projectName: 'Agent Studio',
  },
  promptMarkdown: '# Task Chat',
  statusMarkdown: null,
  promptHistory: [],
  titleHistory: [],
  contextUsage: null,
  log: [],
  summaryState: null,
  reviewEvidence: [],
} as unknown as TaskDetail;

describe('TaskChatComponent', () => {
  it('materializes the managed task context by reading its central transcript on open', async () => {
    const getOrchestratorChatByContext = vi.fn().mockReturnValue(of({
      project: 'Agent Studio',
      turns: [],
    }));
    const fixture = await createFixture({
      getOrchestratorChatByContext,
      sendOrchestratorChatByContext: vi.fn(),
    });

    expect(getOrchestratorChatByContext).toHaveBeenCalledWith(
      'task:Agent Studio/AGT-2574',
    );
    expect(fixture.componentInstance.placeholder()).toBe('Ask about AGT-2574');
  });

  it('sends a mandatory task envelope only through the Orchestrator Chat endpoint', async () => {
    const getOrchestratorChatByContext = vi.fn().mockReturnValue(of({
      project: 'Agent Studio',
      turns: [],
    }));
    const sendOrchestratorChatByContext = vi.fn().mockReturnValue(of({
      project: 'Agent Studio',
      reply: {
        id: 'reply-1',
        ts: '2026-08-10T12:00:01Z',
        role: 'orchestrator',
        text: 'The latest run failed during verification.',
      },
    }));
    const fixture = await createFixture({
      getOrchestratorChatByContext,
      sendOrchestratorChatByContext,
    });

    await fixture.componentInstance.onSubmit({
      text: 'What failed?',
      attachments: [],
    } as ChatSubmitEvent);

    expect(sendOrchestratorChatByContext).toHaveBeenCalledOnce();
    const [contextKey, request] = sendOrchestratorChatByContext.mock.calls[0];
    expect(contextKey).toBe('task:Agent Studio/AGT-2574');
    expect(request).toMatchObject({
      text: 'What failed?',
      navigationContext: {
        currentPage: 'task-detail',
        currentTaskId: 'agt-2574-folder',
        currentTaskKey: 'AGT-2574',
        currentTaskTitle: 'Add Task Chat',
        currentTaskState: '2-ready',
        observedSurface: 'Task Chat',
      },
      contextEnvelope: {
        scope: {
          kind: 'task',
          contextKey: 'task:Agent Studio/AGT-2574',
          projectId: 'Agent Studio',
          taskKey: 'AGT-2574',
        },
        activeSurface: {
          kind: 'task',
          reference: 'AGT-2574',
          taskKey: 'AGT-2574',
        },
        explicitReferences: [],
      },
    });
    expect(getOrchestratorChatByContext).toHaveBeenCalledTimes(2);
    expect(request).not.toHaveProperty('attachments');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="chat-attach"]')).toBeNull();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="chat-toolbar"]')).toBeNull();
  });
});

async function createFixture(service: {
  getOrchestratorChatByContext: ReturnType<typeof vi.fn>;
  sendOrchestratorChatByContext: ReturnType<typeof vi.fn>;
}) {
  await TestBed.configureTestingModule({
    imports: [TaskChatComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideCodingAgentChat(),
      { provide: TaskService, useValue: service },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(TaskChatComponent);
  fixture.componentRef.setInput('detail', DETAIL);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

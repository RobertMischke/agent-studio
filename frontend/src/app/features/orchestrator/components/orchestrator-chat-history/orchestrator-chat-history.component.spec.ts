import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { TaskService } from '../../../../services/task.service';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';
import { OrchestratorChatHistoryComponent } from './orchestrator-chat-history.component';

function context(overrides: Partial<OrchestratorContextSession>): OrchestratorContextSession {
  return {
    contextKey: 'project:Agent Studio',
    kind: 'project',
    projectId: 'Agent Studio',
    taskKey: null,
    updatedAt: '2026-08-10T09:00:00Z',
    model: null,
    cumulativeInputTokens: 0,
    cumulativeOutputTokens: 0,
    cumulativeCacheReadTokens: 0,
    cumulativeCacheCreationTokens: 0,
    runtimeStatus: 'idle',
    queuePosition: 0,
    summary: 'Project chat for Agent Studio',
    ...overrides,
  };
}

class TaskServiceStub {
  sessions: OrchestratorContextSession[] = [];

  getOrchestratorContextSessions() {
    return of({ sessions: this.sessions });
  }
}

class JobsHubClientStub {
  readonly connected = signal(true);
  readonly orchestratorContextsRevision = signal(0);
}

describe('OrchestratorChatHistoryComponent', () => {
  function mount(sessions: OrchestratorContextSession[]) {
    const tasks = new TaskServiceStub();
    tasks.sessions = sessions;
    const hub = new JobsHubClientStub();
    TestBed.configureTestingModule({
      imports: [OrchestratorChatHistoryComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: tasks },
        { provide: JobsHubClient, useValue: hub },
      ],
    });
    const fixture = TestBed.createComponent(OrchestratorChatHistoryComponent);
    fixture.detectChanges();
    return { fixture, tasks, hub };
  }

  it('groups project, task, and Dossier contexts and shows their summaries', () => {
    const { fixture } = mount([
      context({
        contextKey: 'task:Agent Studio/AGT-2577',
        kind: 'task',
        taskKey: 'AGT-2577',
        summary: 'Build the central Chat History view.',
        updatedAt: '2026-08-10T10:00:00Z',
      }),
      context({}),
      context({
        contextKey: 'dossier:Agent Studio/routing',
        kind: 'dossier',
        taskKey: null,
        dossierId: 'routing',
        dossierKey: 'AGT-W34',
        dossierTitle: 'Routing context',
        dossierState: 'active',
        summary: 'Dossier-only conversation.',
      }),
    ]);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelectorAll('[data-testid="chat-history-project-list"] [data-testid="chat-history-row"]')).toHaveLength(1);
    expect(host.querySelectorAll('[data-testid="chat-history-task-list"] [data-testid="chat-history-row"]')).toHaveLength(1);
    expect(host.querySelectorAll('[data-testid="chat-history-dossier-list"] [data-testid="chat-history-row"]')).toHaveLength(1);
    expect(host.textContent).toContain('Build the central Chat History view.');
    expect(host.textContent).toContain('1 project context');
    expect(host.textContent).toContain('1 task context');
    expect(host.textContent).toContain('1 Dossier context');
    expect(host.textContent).toContain('AGT-W34');
  });

  it('emits the stable context key when a row opens chat', () => {
    const { fixture } = mount([context({})]);
    const opened: string[] = [];
    fixture.componentInstance.contextOpened.subscribe(key => opened.push(key));

    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="chat-history-row"]')!
      .click();

    expect(opened).toEqual(['project:Agent Studio']);
  });

  it('reloads from the Task Server projection after a SignalR refresh hint', () => {
    const { fixture, tasks, hub } = mount([context({})]);
    tasks.sessions = [
      context({}),
      context({
        contextKey: 'task:Agent Studio/AGT-2577',
        kind: 'task',
        taskKey: 'AGT-2577',
        summary: 'Live update arrived.',
      }),
    ];

    hub.orchestratorContextsRevision.update(value => value + 1);
    TestBed.tick();
    fixture.detectChanges();

    expect(fixture.componentInstance.contexts()).toHaveLength(2);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Live update arrived.');
  });

  it('marks a running context as active current state', () => {
    const { fixture } = mount([context({ runtimeStatus: 'active' })]);
    const host = fixture.nativeElement as HTMLElement;
    const row = host.querySelector<HTMLElement>('[data-testid="chat-history-row"]')!;

    expect(row.classList).toContain('chat-history__row--working');
    expect(row.dataset['runtimeStatus']).toBe('active');
    expect(host.querySelector('[data-testid="chat-history-active-count"]')?.textContent).toContain('1');
    expect(host.querySelector('[data-testid="chat-history-runtime-status"]')?.textContent).toContain('Running');
  });
});

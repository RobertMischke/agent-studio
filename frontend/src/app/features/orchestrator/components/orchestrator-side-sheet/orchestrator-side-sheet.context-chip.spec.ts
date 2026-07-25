import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { ChatComponent } from 'coding-agent-chat/composer';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

describe('OrchestratorSideSheetComponent context badge and menu', () => {
  async function makeFixture() {
    await TestBed.configureTestingModule({
      imports: [OrchestratorSideSheetComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['demo-project']);
    fixture.componentInstance.activeProject.set('demo-project');
    return fixture;
  }

  it('counts every distinct global, project and task context', async () => {
    const fixture = await makeFixture();
    const session = (contextKey: string, kind: OrchestratorContextSession['kind']): OrchestratorContextSession => ({
      contextKey,
      kind,
      projectId: kind === 'global' ? null : 'demo-project',
      taskKey: kind === 'task' ? 'AGT-2087' : null,
      updatedAt: '2026-07-11T10:00:00Z',
      model: null,
      cumulativeInputTokens: 0,
      cumulativeOutputTokens: 0,
      cumulativeCacheReadTokens: 0,
      cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'idle',
      queuePosition: 0,
    });

    fixture.componentInstance.contextSessions.set([
      session('project:demo-project', 'project'),
      session('task:demo-project/AGT-2087', 'task'),
    ]);

    expect(fixture.componentInstance.contextCount()).toBe(3);
  });

  it('keeps only picker and count badge in the collapsed header', async () => {
    const fixture = await makeFixture();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="orch-context-badge"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orch-context-count"]')?.textContent?.trim()).toBe('2');
    expect(root.querySelector('[data-testid="orch-context-menu"]')).toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-pin"]')).toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-settings"]')).toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-refresh"]')).toBeNull();
  });

  it('opens the full context menu and moves header actions into it', async () => {
    const fixture = await makeFixture();
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    (root.querySelector('[data-testid="orch-context-badge"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="orch-context-menu"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="chat-context-list"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-pin"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-settings"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-refresh"]')).not.toBeNull();
  });

  it('explains every context action in one sentence at the option', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('activeJobId', 'task-id');
    fixture.componentRef.setInput('activeJobTitle', 'Task title');
    fixture.componentRef.setInput('activeWatchPath', '/workspace/project');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    (root.querySelector('[data-testid="orch-context-badge"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const explanation = (testId: string) => fixture.debugElement
      .query(By.css(`[data-testid="${testId}"]`))
      .injector.get(AppTooltipDirective)
      .appTooltip();

    expect(explanation('orch-side-sheet-pin')).toBe(
      'Pin this context when you want the chat to stay on the current project or task while you navigate elsewhere.',
    );
    expect(explanation('orch-side-sheet-verbose-debug')).toBe(
      'Open verbose runtime details when you need to investigate this task chat.',
    );
    expect(explanation('orch-side-sheet-settings')).toBe(
      'Open Orchestrator settings when you need to change its behavior or defaults.',
    );
    expect(explanation('orch-side-sheet-refresh')).toBe(
      'Reload this context digest and chat when you need the latest available state.',
    );
    expect(explanation('orch-context-send-toggle')).toBe(
      'Send your next message without the current context when it would distract from the request.',
    );

    fixture.componentInstance.togglePin();
    fixture.componentInstance.toggleNextMessageContext();
    fixture.detectChanges();
    expect(explanation('orch-side-sheet-pin')).toBe(
      'Follow navigation when you want this chat to track the project or task you open.',
    );
    expect(explanation('orch-context-send-toggle')).toBe(
      'Include the current context with your next message when it is relevant to the request.',
    );
  });

  it('renders the standard composer footer once and removes both host task workflows', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('composerContext', { project: 'Agent Studio', surface: 'Board' });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelectorAll('[data-testid="chat-composer-foot"]')).toHaveLength(1);
    expect(root.querySelector('[data-testid="chat-composer-context-project"]')?.textContent?.trim())
      .toBe('Agent Studio');
    expect(root.querySelector('[data-testid="chat-composer-context-surface"]')?.textContent?.trim())
      .toBe('Board');
    expect(root.textContent).not.toContain('Make a task from your message');
    expect(root.textContent).not.toContain('Make a task from this reply');
    expect(root.querySelector('[data-testid="orch-side-sheet-draft-actions"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-toolbar-task"]')).toBeNull();
  });

  it('forwards live active-tab context without remounting CAC or losing its draft', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('composerContext', { project: 'Agent Studio', surface: 'Board' });
    fixture.detectChanges();
    const firstChat = fixture.debugElement.query(By.directive(ChatComponent)).componentInstance as ChatComponent;
    const textarea = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLTextAreaElement>('[data-testid="chat-input"]')!;
    textarea.value = 'Draft survives navigation';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    fixture.componentRef.setInput('composerContext', {
      project: 'Agent Studio',
      surface: 'Task',
      detail: 'AGT-2162',
    });
    fixture.detectChanges();

    const secondChat = fixture.debugElement.query(By.directive(ChatComponent)).componentInstance as ChatComponent;
    expect(secondChat).toBe(firstChat);
    expect(textarea.value).toBe('Draft survives navigation');
    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="chat-composer-context-surface"]')?.textContent?.trim()).toBe('Task');
    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="chat-composer-context-detail"]')?.textContent?.trim()).toBe('AGT-2162');
  });
});

import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

describe('OrchestratorSideSheetComponent multichat rail', () => {
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

  it('counts only active and queued contexts in the collapsed chip', async () => {
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
      runtimeStatus: kind === 'project' ? 'active' : 'queued',
      queuePosition: 0,
    });

    fixture.componentInstance.contextSessions.set([
      session('project:demo-project', 'project'),
      session('task:demo-project/AGT-2087', 'task'),
    ]);

    expect(fixture.componentInstance.activeContextCount()).toBe(2);
  });

  it('keeps the rail collapsed while context controls and chat remain available', async () => {
    const fixture = await makeFixture();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="orch-context-badge"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orch-context-count"]')?.textContent?.trim()).toBe('0');
    expect(root.querySelector('[data-testid="orch-context-menu"]')).toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-pin"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orch-side-sheet-settings"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orchestrator-conversation"]')).not.toBeNull();
  });

  it('opens the rail beside the chat instead of replacing it', async () => {
    const fixture = await makeFixture();
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    (root.querySelector('[data-testid="orch-context-badge"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="orch-context-menu"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="chat-context-list"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="orchestrator-conversation"]')).not.toBeNull();
  });

  it('keeps row-name chat switching separate from arrow navigation', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const navigated = vi.fn();
    component.navigateToContext.subscribe(navigated);
    component.contextSessions.set([{
      contextKey: 'task:demo-project/AGT-2087',
      kind: 'task',
      projectId: 'demo-project',
      taskKey: 'AGT-2087',
      updatedAt: '2026-07-11T10:00:00Z',
      model: null,
      cumulativeInputTokens: 0,
      cumulativeOutputTokens: 0,
      cumulativeCacheReadTokens: 0,
      cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'parked',
      queuePosition: 0,
    }]);
    component.railOpen.set(true);

    component.selectChatContext('task:demo-project/AGT-2087');
    expect(component.contextKey()).toBe('task:demo-project/AGT-2087');
    expect(component.railOpen()).toBe(true);
    expect(navigated).not.toHaveBeenCalled();

    component.onNavigateToContext('task:demo-project/AGT-2087');
    expect(navigated).toHaveBeenCalledWith('task:demo-project/AGT-2087');
    expect(component.selectedContextKey()).toBeNull();
  });
});

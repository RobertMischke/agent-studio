import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
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
});

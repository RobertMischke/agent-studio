import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import { UiPreferencesService } from '../../../shell/state/ui-preferences.service';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

describe('OrchestratorSideSheetComponent project entry', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [OrchestratorSideSheetComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
  });

  it('waits for route hydration, then opens in the resolved project context', () => {
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Persisted project', 'Resolved project']);
    fixture.componentRef.setInput('projectEntryReady', false);
    fixture.componentRef.setInput('preferredProject', 'Persisted project');
    fixture.componentRef.setInput('composerContext', { project: 'Persisted project', surface: 'Board' });
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(false);

    fixture.componentRef.setInput('preferredProject', 'Resolved project');
    fixture.componentRef.setInput('composerContext', { project: 'Resolved project', surface: 'Overview' });
    fixture.componentRef.setInput('projectEntryReady', true);
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(true);
    expect(fixture.componentInstance.activeProject()).toBe('Resolved project');
    expect(fixture.componentInstance.contextKey()).toBe('project:Resolved project');
    expect(document.activeElement).not.toBe(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="chat-input"]'),
    );
  });

  it('respects the saved opt-out and leaves the status-bar toggle path available', () => {
    TestBed.inject(UiPreferencesService).setOpenProjectChatOnEntry(false);
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Quiet project']);
    fixture.componentRef.setInput('preferredProject', 'Quiet project');
    fixture.componentRef.setInput('composerContext', { project: 'Quiet project', surface: 'Board' });
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(false);
    fixture.componentInstance.toggle();
    expect(fixture.componentInstance.open()).toBe(true);
  });

  it('does not turn a task deep link into the project side-sheet entry', () => {
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Task project']);
    fixture.componentRef.setInput('preferredProject', 'Task project');
    fixture.componentRef.setInput('composerContext', {
      project: 'Task project',
      surface: 'Task',
      taskKey: 'AGT-2576',
      taskId: 'agt-2576',
    });
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(false);
  });

  it('keeps a manually closed panel closed within one project and reopens on the next project', () => {
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Alpha', 'Beta']);
    fixture.componentRef.setInput('preferredProject', 'Alpha');
    fixture.componentRef.setInput('composerContext', { project: 'Alpha', surface: 'Board' });
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);

    fixture.componentInstance.hide();
    fixture.componentRef.setInput('composerContext', { project: 'Alpha', surface: 'Wiki' });
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(false);

    fixture.componentRef.setInput('preferredProject', 'Beta');
    fixture.componentRef.setInput('composerContext', { project: 'Beta', surface: 'Board' });
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(true);
    expect(fixture.componentInstance.contextKey()).toBe('project:Beta');
  });
});

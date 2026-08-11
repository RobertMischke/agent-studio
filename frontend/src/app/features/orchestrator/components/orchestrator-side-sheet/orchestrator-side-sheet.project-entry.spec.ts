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
    sessionStorage.clear();
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

  it('does not open while navigation moves between project and task contexts', () => {
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Alpha', 'Beta']);
    fixture.componentRef.setInput('preferredProject', 'Alpha');
    fixture.componentRef.setInput('composerContext', { project: 'Alpha', surface: 'Board' });
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(false);

    fixture.componentRef.setInput('preferredProject', 'Beta');
    fixture.componentRef.setInput('composerContext', { project: 'Beta', surface: 'Dossier' });
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(false);

    fixture.componentRef.setInput('composerContext', {
      project: 'Beta', surface: 'Task', taskKey: 'BET-1', taskId: 'bet-1',
    });
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(false);
    expect(fixture.componentInstance.contextKey()).toBe('task:Beta/BET-1');
  });

  it('uses the default entry only for an explicit project entry without saved posture', () => {
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Entry project']);
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(false);
    fixture.componentInstance.openForProjectEntry('Entry project');
    expect(fixture.componentInstance.open()).toBe(true);
    expect(fixture.componentInstance.contextKey()).toBe('project:Entry project');
    expect(sessionStorage.getItem('atp.studio.orchestratorOpen.v1')).toBe('1');
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

  it('keeps an explicitly closed posture through later project entries', () => {
    sessionStorage.setItem('atp.studio.orchestratorOpen.v1', '0');
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Alpha', 'Beta']);
    fixture.detectChanges();

    fixture.componentInstance.openForProjectEntry('Alpha');
    fixture.componentInstance.openForProjectEntry('Beta');

    expect(fixture.componentInstance.open()).toBe(false);
    expect(fixture.componentInstance.contextKey()).toBe('project:Beta');
  });

  it('respects the saved empty-entry opt-out while explicit toggles remain available', () => {
    TestBed.inject(UiPreferencesService).setOpenProjectChatOnEntry(false);
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Quiet project']);
    fixture.detectChanges();

    fixture.componentInstance.openForProjectEntry('Quiet project');
    expect(fixture.componentInstance.open()).toBe(false);
    fixture.componentInstance.toggle();
    expect(fixture.componentInstance.open()).toBe(true);
  });
});

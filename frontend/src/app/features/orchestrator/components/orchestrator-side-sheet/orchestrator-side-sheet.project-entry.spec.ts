import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

describe('OrchestratorSideSheetComponent panel state', () => {
  function configure(): void {
    TestBed.configureTestingModule({
      imports: [OrchestratorSideSheetComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
  }

  function mount(project = 'Alpha') {
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['Alpha', 'Beta']);
    fixture.componentRef.setInput('preferredProject', project);
    fixture.componentRef.setInput('composerContext', { project, surface: 'Board' });
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    configure();
  });

  it('does not open for project or page navigation', () => {
    const fixture = mount();
    const component = fixture.componentInstance;
    expect(component.open()).toBe(false);

    fixture.componentRef.setInput('preferredProject', 'Beta');
    fixture.componentRef.setInput('composerContext', { project: 'Beta', surface: 'Dossier', detail: 'Decision log' });
    fixture.detectChanges();

    expect(component.open()).toBe(false);
    expect(component.contextKey()).toBe('project:Beta');
  });

  it('keeps manual open state and width through navigation and component recreation', () => {
    const fixture = mount();
    fixture.componentInstance.show();
    fixture.componentInstance.startResize(new MouseEvent('mousedown', { clientX: 900 }));
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: 820 }));
    window.dispatchEvent(new MouseEvent('mouseup'));
    const width = fixture.componentInstance.panelWidth();

    fixture.componentRef.setInput('preferredProject', 'Beta');
    fixture.componentRef.setInput('composerContext', { project: 'Beta', surface: 'Wiki', detail: 'Runbook' });
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);
    expect(fixture.componentInstance.contextKey()).toBe('project:Beta');

    fixture.destroy();
    TestBed.resetTestingModule();
    configure();
    const restored = mount('Beta');
    expect(restored.componentInstance.open()).toBe(true);
    expect(restored.componentInstance.panelWidth()).toBe(width);
  });

  it('uses the S5 standard entry only before an operator posture exists', () => {
    const fixture = mount();
    const component = fixture.componentInstance;

    component.showForStandardProjectEntry();
    expect(component.open()).toBe(true);
    component.hide();
    component.showForStandardProjectEntry();

    expect(component.open()).toBe(false);
    expect(sessionStorage.getItem('atp.studio.orchestratorOpen.v1')).toBe('0');
  });
});

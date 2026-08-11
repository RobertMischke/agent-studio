import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { OrchestratorPanelHeaderComponent } from './orchestrator-panel-header.component';

describe('OrchestratorPanelHeaderComponent', () => {
  it('uses the task identity as the primary header context', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorPanelHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorPanelHeaderComponent);
    fixture.componentRef.setInput('project', 'Agent Studio');
    fixture.componentRef.setInput('taskKey', 'AGT-2613');
    fixture.componentRef.setInput('taskTitle', 'Repair the panel frame');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="orch-panel-context-type"]')?.textContent).toContain(
      'Task',
    );
    expect(host.querySelector('[data-testid="orch-panel-context-name"]')?.textContent).toContain(
      'AGT-2613 · Repair the panel frame',
    );
  });

  it('lets a Dossier page replace the project fallback without changing the action row', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorPanelHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorPanelHeaderComponent);
    fixture.componentRef.setInput('project', 'Agent Studio');
    fixture.componentRef.setInput('pageContext', {
      projectName: 'Agent Studio',
      relPath: 'concepts/panel-frame.html',
      title: 'AGT-W34',
      pageType: 'workbench',
      excerpt: '',
    });
    fixture.componentRef.setInput('contextCount', 17);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="orch-panel-context-type"]')?.textContent).toContain(
      'Dossier',
    );
    expect(host.querySelector('[data-testid="orch-panel-context-name"]')?.textContent).toContain(
      'AGT-W34',
    );
    expect(host.querySelector('[data-testid="orch-context-count"]')?.textContent).toContain('17');
  });

  it('shows a first-class Dossier identity and the active chat count', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorPanelHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorPanelHeaderComponent);
    fixture.componentRef.setInput('project', 'Agent Studio');
    fixture.componentRef.setInput('dossierId', 'context-model');
    fixture.componentRef.setInput('dossierTitle', 'AGT-W34');
    fixture.componentRef.setInput('activeChatCount', 2);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="orch-panel-context-type"]')?.textContent).toContain('Dossier');
    expect(host.querySelector('[data-testid="orch-panel-context-name"]')?.textContent).toContain('AGT-W34');
    expect(host.querySelector('[data-testid="orch-active-chat-count"]')?.textContent).toContain('2 active');
  });
});

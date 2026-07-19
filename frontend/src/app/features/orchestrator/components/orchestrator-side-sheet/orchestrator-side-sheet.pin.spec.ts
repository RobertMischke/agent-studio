import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

/**
 * MC-2 (Concept §4) unit coverage for the navigation-following context and
 * the Pin freeze. Drives the component's public signals directly (the same
 * pattern as the context-chip spec) so the effective-context derivation and
 * the pin snapshot are pinned without a full host-app render. `detectChanges`
 * is deliberately never called so the preferred-sync / refresh effects do not
 * clobber the manually-seeded `activeProject`.
 */
describe('OrchestratorSideSheetComponent · navigation context + pin', () => {
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
    return TestBed.createComponent(OrchestratorSideSheetComponent);
  }

  it('derives a project context on the board and a task context on a task page', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;

    c.activeProject.set('demo-project');
    expect(c.contextKind()).toBe('project');
    expect(c.contextKey()).toBe('project:demo-project');

    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    fixture.componentRef.setInput('activeJobKey', 'AGT-1916');
    expect(c.contextKind()).toBe('task');
    expect(c.contextKey()).toBe('task:demo-project/AGT-1916');
  });

  it('effective scope follows live navigation inputs while unpinned', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;

    c.activeProject.set('demo-project');
    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    fixture.componentRef.setInput('activeJobKey', 'AGT-1916');
    fixture.componentRef.setInput('activeJobState', '3-progress');

    expect(c.effectiveProject()).toBe('demo-project');
    expect(c.effectiveJobId()).toBe('job-1');
    expect(c.effectiveJobTitle()).toBe('Fix the header');
    expect(c.effectiveJobKey()).toBe('AGT-1916');
    expect(c.effectiveJobState()).toBe('3-progress');
  });

  it('pin freezes the context so later navigation does not switch it', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;

    c.activeProject.set('demo-project');
    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    fixture.componentRef.setInput('activeJobKey', 'AGT-1916');
    fixture.componentRef.setInput('activeJobState', '3-progress');

    c.togglePin();
    expect(c.pinned()).toBe(true);
    const frozenKey = c.contextKey();
    expect(frozenKey).toBe('task:demo-project/AGT-1916');

    // Navigate elsewhere: the host swaps in a different task, and the picker
    // is inert while pinned. The effective scope must not move.
    fixture.componentRef.setInput('activeJobId', 'job-2');
    fixture.componentRef.setInput('activeJobTitle', 'Something else');
    fixture.componentRef.setInput('activeJobKey', 'AGT-2000');
    c.setActiveProject('other-project');

    expect(c.effectiveProject()).toBe('demo-project');
    expect(c.effectiveJobId()).toBe('job-1');
    expect(c.effectiveJobTitle()).toBe('Fix the header');
    expect(c.contextKey()).toBe(frozenKey);
    // The picker was blocked, so the live project signal never moved either.
    expect(c.activeProject()).toBe('demo-project');
  });

  it('unpin resumes following navigation', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;

    c.activeProject.set('demo-project');
    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    fixture.componentRef.setInput('activeJobKey', 'AGT-1916');

    c.togglePin();
    // Navigation moves on while pinned...
    fixture.componentRef.setInput('activeJobId', 'job-2');
    fixture.componentRef.setInput('activeJobTitle', 'Something else');
    fixture.componentRef.setInput('activeJobKey', 'AGT-2000');

    c.togglePin();
    expect(c.pinned()).toBe(false);
    // Effective scope now reflects the live inputs again.
    expect(c.effectiveJobId()).toBe('job-2');
    expect(c.effectiveJobTitle()).toBe('Something else');
    expect(c.contextKey()).toBe('task:demo-project/AGT-2000');
  });

  it('context label tracks the effective frozen scope while pinned', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;

    c.activeProject.set('demo-project');
    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    c.togglePin();

    // Host navigates to the board of another project.
    c.setActiveProject('other-project');
    fixture.componentRef.setInput('activeJobId', null);
    fixture.componentRef.setInput('activeJobTitle', null);

    expect(c.contextChipText()).toBe(`Context: demo-project · Task 'Fix the header'`);
  });

  // MC-2: the chat body must actually READ the per-context thread, not just
  // derive the key. A task page reads the `task:<PROJ>/<KEY>` route; the board
  // reads the `project:<PROJ>` route. This is the wiring the B-grade review
  // flagged as missing ("chat still reads the project thread").
  it('refresh reads the task-context transcript route on a task page', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);

    c.activeProject.set('demo-project');
    fixture.componentRef.setInput('activeJobId', 'job-1');
    fixture.componentRef.setInput('activeJobTitle', 'Fix the header');
    fixture.componentRef.setInput('activeJobKey', 'AGT-1916');
    expect(c.contextKey()).toBe('task:demo-project/AGT-1916');

    c.refresh(true);
    const req = http.expectOne('/api/runner/task:demo-project/AGT-1916/orchestrator-chat');
    expect(req.request.method).toBe('GET');
    req.flush({
      project: 'demo-project',
      turns: [{ id: 't1', ts: '2026-07-09T00:00:00Z', role: 'user', text: 'task thread' }],
    });
    expect(c.turns().map((t) => t.text)).toEqual(['task thread']);
    http.verify();
  });

  it('refresh reads the project-context transcript route on the board', async () => {
    const fixture = await makeFixture();
    const c = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);

    c.activeProject.set('demo-project');
    expect(c.contextKey()).toBe('project:demo-project');

    c.refresh(true);
    const req = http.expectOne('/api/runner/project:demo-project/orchestrator-chat');
    expect(req.request.method).toBe('GET');
    req.flush({ project: 'demo-project', turns: [] });
    http.verify();
  });
});

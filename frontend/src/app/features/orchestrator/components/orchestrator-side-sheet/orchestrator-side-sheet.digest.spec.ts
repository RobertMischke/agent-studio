import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import type { OrchestratorContextDigest } from '../../models/orchestrator.model';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

describe('OrchestratorSideSheetComponent · ORCH-1 context digest', () => {
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

  function digest(contextKey: string, text = 'lanes: ready=2'): OrchestratorContextDigest {
    return {
      contextKey,
      capturedAt: new Date().toISOString(),
      digest: text,
      sources: [
        { name: 'lanes', status: 'ok', capturedAt: new Date().toISOString(), detail: null },
      ],
    };
  }

  function flushSessionReads(http: HttpTestingController): void {
    for (const request of http.match('/api/orchestrator/sessions')) {
      request.flush({ sessions: [] });
    }
  }

  it('uses the visible Refresh action to force context, then reconcile chat and sessions', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.activeProject.set('Agent Studio');
    fixture.detectChanges();
    flushSessionReads(http);

    component.contextDigestState.selectContext('project:Agent Studio');
    component.contextDigestState.digest.set(digest('project:Agent Studio'));
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="orch-context-badge"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-scope"]')?.textContent.trim())
      .toBe('Project context');
    expect(fixture.nativeElement.querySelector('[data-testid="orch-context-freshness"]')?.textContent)
      .toContain('Context captured');

    (fixture.nativeElement.querySelector('[data-testid="orch-side-sheet-refresh"]') as HTMLButtonElement).click();
    const contextRequest = http.expectOne('/api/orchestrator/context/project:Agent%20Studio/refresh');
    expect(contextRequest.request.method).toBe('POST');
    contextRequest.flush(digest('project:Agent Studio', 'lanes: progress=1'));
    http.expectOne('/api/runner/project:Agent%20Studio/orchestrator-chat').flush({
      project: 'Agent Studio',
      turns: [],
    });
    flushSessionReads(http);

    expect(component.contextDigestState.digest()?.digest).toBe('lanes: progress=1');
    expect(component.contextDigestState.error()).toBeNull();
    http.verify();
    fixture.destroy();
  });

  it('preserves the last good digest when a forced refresh fails', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.activeProject.set('demo-project');
    const previous = digest('project:demo-project');
    component.contextDigestState.selectContext('project:demo-project');
    component.contextDigestState.digest.set(previous);

    component.refreshCurrentContext();
    http.expectOne('/api/orchestrator/context/project:demo-project/refresh').flush(
      { error: 'quota probe unavailable' },
      { status: 503, statusText: 'Service Unavailable' },
    );
    http.expectOne('/api/runner/project:demo-project/orchestrator-chat').flush({
      project: 'demo-project',
      turns: [],
    });
    flushSessionReads(http);

    expect(component.contextDigestState.digest()).toBe(previous);
    expect(component.contextDigestState.error()).toBe('quota probe unavailable');
    expect(component.contextDigestState.statusText()).toContain('Refresh failed');
    http.verify();
    fixture.destroy();
  });

  it('does not let an older background read overwrite a forced refresh', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.activeProject.set('demo-project');
    component.show();
    fixture.detectChanges();
    flushSessionReads(http);
    const background = http.expectOne('/api/orchestrator/context/project:demo-project');
    http.expectOne('/api/runner/project:demo-project/orchestrator-chat').flush({
      project: 'demo-project',
      turns: [],
    });

    component.refreshCurrentContext();
    http.expectOne('/api/orchestrator/context/project:demo-project/refresh')
      .flush(digest('project:demo-project', 'new forced digest'));
    http.expectOne('/api/runner/project:demo-project/orchestrator-chat').flush({
      project: 'demo-project',
      turns: [],
    });
    flushSessionReads(http);
    background.flush(digest('project:demo-project', 'older background digest'));

    expect(component.contextDigestState.digest()?.digest).toBe('new forced digest');
    http.verify();
    fixture.destroy();
  });

  it('loads the global digest on open without enabling a global transcript', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component.activeProject.set('previous-project');
    fixture.detectChanges();
    flushSessionReads(http);

    component.selectChatContext('global');
    component.show();
    fixture.detectChanges();
    expect(component.effectiveProject()).toBeNull();
    expect(component.effectiveJobId()).toBeNull();
    http.expectOne('/api/orchestrator/context/global').flush(digest('global', 'workspace digest'));
    http.expectNone('/api/runner/global/orchestrator-chat');
    fixture.detectChanges();

    expect(component.contextDigestState.scopeLabel()).toBe('Global context');
    expect(fixture.nativeElement.querySelector('[data-testid="orchestrator-global-chat-empty"]'))
      .toBeTruthy();
    http.verify();
    fixture.destroy();
  });

  it('self-heals a stale selection and shares the spaced task key across digest, chat and send', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('projects', ['Agent Studio']);
    fixture.componentRef.setInput('activeJobId', 'agent-studio-task');
    fixture.componentRef.setInput('activeJobTitle', 'Repair orchestrator context');
    fixture.componentRef.setInput('activeJobKey', 'AGT-2149');
    component.activeProject.set('Agent Studio');
    fixture.detectChanges();
    flushSessionReads(http);

    component.selectedContextKey.set('task:old-project/AGT-1/invalid');
    fixture.detectChanges();
    expect(component.selectedContextKey()).toBeNull();
    expect(component.contextKey()).toBe('task:Agent Studio/AGT-2149');

    component.refreshCurrentContext();
    http.expectOne('/api/orchestrator/context/task:Agent%20Studio/AGT-2149/refresh')
      .flush(digest('task:Agent Studio/AGT-2149'));
    http.expectOne('/api/runner/task:Agent%20Studio/AGT-2149/orchestrator-chat')
      .flush({ project: 'Agent Studio', turns: [] });
    flushSessionReads(http);

    await component.onSubmit({ text: 'Please continue', attachments: [] });
    const send = http.expectOne('/api/runner/task:Agent%20Studio/AGT-2149/orchestrator-chat');
    expect(send.request.method).toBe('POST');
    send.flush({ project: 'Agent Studio', reply: { id: 'reply', role: 'orchestrator', text: 'Done' } });
    http.expectOne('/api/runner/task:Agent%20Studio/AGT-2149/orchestrator-chat')
      .flush({ project: 'Agent Studio', turns: [] });

    http.verify();
    fixture.destroy();
  });
});

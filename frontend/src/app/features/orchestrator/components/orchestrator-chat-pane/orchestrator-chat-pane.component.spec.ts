import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { OrchestratorChatPaneComponent } from './orchestrator-chat-pane.component';

describe('OrchestratorChatPaneComponent', () => {
  async function makeFixture(contextKey: string) {
    await TestBed.configureTestingModule({
      imports: [OrchestratorChatPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorChatPaneComponent);
    fixture.componentRef.setInput('project', 'demo-project');
    fixture.componentRef.setInput('contextKey', contextKey);
    fixture.detectChanges();
    return fixture;
  }

  it('reads a task context transcript independently from the side-sheet host', async () => {
    const fixture = await makeFixture('task:demo-project/AGT-1916');
    const http = TestBed.inject(HttpTestingController);

    fixture.componentInstance.refresh(true);
    const request = http.expectOne('/api/runner/task:demo-project/AGT-1916/orchestrator-chat');
    expect(request.request.method).toBe('GET');
    request.flush({
      project: 'demo-project',
      turns: [{ id: 't1', ts: '2026-07-09T00:00:00Z', role: 'user', text: 'task thread' }],
    });

    expect(fixture.componentInstance.turns().map(turn => turn.text)).toEqual(['task thread']);
    http.verify();
  });

  it('keeps a project context on the canonical project transcript route', async () => {
    const fixture = await makeFixture('project:demo-project');
    const http = TestBed.inject(HttpTestingController);

    fixture.componentInstance.refresh(true);
    http.expectOne('/api/runner/project:demo-project/orchestrator-chat')
      .flush({ project: 'demo-project', turns: [] });

    http.verify();
  });
});

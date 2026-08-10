import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

describe('OrchestratorSideSheetComponent context attachments', () => {
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
    fixture.detectChanges();
    return fixture;
  }

  it('uses CAC context chips without a toolbar or image affordance', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('.sidesheet__title')?.textContent?.trim()).toBe('Chat');
    expect(root.querySelector('[data-testid="chat-input"]')?.getAttribute('placeholder'))
      .toContain('Ask a question');
    expect(root.querySelector('[data-testid="orch-composer-add"]')).not.toBeNull();
    expect(component.composerActionMenuItems).toEqual([
      { kind: 'row', id: 'add-context', label: 'Add context' },
    ]);
    expect(root.querySelector('[data-testid="chat-toolbar"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-attach"]')).toBeNull();
    expect(root.querySelector('input[type="file"]')).toBeNull();

    root.querySelector<HTMLButtonElement>('[data-testid="orch-composer-add"]')!.click();
    expect(component.composerActionMenuOpen()).toBe(true);
    component.onComposerActionMenuItem({
      id: 'add-context',
      item: { kind: 'row', id: 'add-context', label: 'Add context' },
    });
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="orch-context-source-picker"]')).not.toBeNull();
  });

  it('adds, removes, and submits a free repository reference through the context envelope', async () => {
    const fixture = await makeFixture();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    const reference = {
      kind: 'repository-file' as const,
      reference: 'docs/context-proof.md',
      projectId: 'demo-project',
    };
    const source = {
      id: 'repository-file:demo-project:docs/context-proof.md',
      category: 'files' as const,
      label: 'docs/context-proof.md',
      detail: 'Repository file',
      estimateTokens: 700,
      reference,
    };

    component.addContextAttachment(source);
    fixture.detectChanges();
    expect(component.cacContextAttachments()).toEqual([{
      id: source.id,
      label: source.label,
      hint: source.detail,
    }]);
    expect((fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="chat-context-attachments"]')?.textContent)
      .toContain('docs/context-proof.md');

    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[aria-label="Remove docs/context-proof.md from context"]')!
      .click();
    fixture.detectChanges();
    expect(component.contextAttachments()).toEqual([]);
    component.addContextAttachment(source);

    await component.onSubmit({ text: 'Use the attached context.', attachments: [] });
    const route = '/api/runner/project:demo-project/orchestrator-chat';
    const send = http.expectOne(route);
    expect(send.request.method).toBe('POST');
    expect(send.request.body).not.toHaveProperty('attachments');
    expect(send.request.body.contextEnvelope.explicitReferences).toEqual([reference]);
    send.flush({
      project: 'demo-project',
      reply: { id: 'reply', role: 'orchestrator', text: 'Used it.' },
    });
    http.expectOne(route).flush({ project: 'demo-project', turns: [] });
    for (const sessions of http.match('/api/orchestrator/sessions')) {
      sessions.flush({ sessions: [] });
    }
    expect(component.contextAttachments()).toEqual([]);
    http.verify();
  });
});

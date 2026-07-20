import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { ProjectUrlsPanelComponent } from './project-urls-panel.component';
import type { RegistryWorkspaceListItem } from '../../../../models/task.model';
import { ProjectUrlRecoveryService } from '../../services/project-url-recovery.service';

function workspacesWith(urls: RegistryWorkspaceListItem['projects'][number]['urls']): RegistryWorkspaceListItem[] {
  return [{
    id: 'ws-1', displayName: 'WS', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-01-01T00:00:00Z',
    projects: [{
      sourceType: 'local-folder', id: 'PROJ-001', displayName: 'Demo', shortCode: 'DEM', workspaceId: 'ws-1',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
      storageLocation: 'c:/demo', repositoryPath: null, rootPath: null, repositoryUrl: null,
      urls, archived: false, createdAt: '2026-01-01T00:00:00Z',
    }],
  }];
}

function mount(projectName = 'Demo') {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });
  const fixture = TestBed.createComponent(ProjectUrlsPanelComponent);
  fixture.componentRef.setInput('projectName', projectName);
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  return { fixture, http };
}

describe('ProjectUrlsPanelComponent', () => {
  it('renders one row per configured URL after load', () => {
    const { fixture, http } = mount();
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([
      { id: 'url-1', label: 'Dev frontend', url: 'http://localhost:4010', sortOrder: 0, startRule: null },
    ]));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    const row = el.querySelector('[data-testid="project-urls-row-url-1"]');
    expect(row).toBeTruthy();
    expect(row?.textContent).toContain('Dev frontend');
    expect(el.querySelector('[data-testid="project-urls-add"]')).toBeTruthy();
  });

  it('shows the empty state when a registry project has no URLs', () => {
    const { fixture, http } = mount();
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([]));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="project-urls-empty"]')).toBeTruthy();
  });

  it('posts a new URL and reflects the returned record', () => {
    const { fixture, http } = mount();
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([]));
    fixture.detectChanges();

    const comp = fixture.componentInstance;
    comp.openAdd();
    // openAdd loads suggestions; answer that request so the queue stays clean.
    http.expectOne(req => req.url.endsWith('/PROJ-001/url-suggestions')).flush([]);
    comp.formLabel.set('Stable');
    comp.formUrl.set('http://localhost:4011');
    comp.save();

    const post = http.expectOne(req => req.method === 'POST' && req.url.endsWith('/PROJ-001/urls'));
    expect(post.request.body).toMatchObject({ label: 'Stable', url: 'http://localhost:4011' });
    post.flush({
      id: 'PROJ-001', displayName: 'Demo', shortCode: 'DEM', workspaceId: 'ws-1',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: 'c:/demo',
      urls: [{ id: 'url-1', label: 'Stable', url: 'http://localhost:4011', sortOrder: 0, startRule: null }],
      archived: false, createdAt: '2026-01-01T00:00:00Z',
    });
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="project-urls-row-url-1"]')?.textContent).toContain('Stable');
  });

  it('prefills, tests, and saves the detected Agent Studio Website setup', () => {
    const { fixture, http } = mount();
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([]));
    fixture.componentInstance.openAdd();
    http.expectOne(req => req.url.endsWith('/PROJ-001/url-suggestions')).flush([{
      label: 'Agent Studio Website', url: 'http://localhost:4184',
      command: 'npm start -- --host 127.0.0.1 --port 4184', cwd: '04-angular-static-final',
      port: 4184, source: 'readme',
    }]);
    fixture.componentInstance.fillFromSuggestion(fixture.componentInstance.suggestions()[0]);

    expect(fixture.componentInstance.formCommand()).toBe('npm start -- --host 127.0.0.1 --port 4184');
    expect(fixture.componentInstance.formCwd()).toBe('04-angular-static-final');
    expect(fixture.componentInstance.formPort()).toBe(4184);
    fixture.componentInstance.testSetup();
    const test = http.expectOne(req => req.method === 'POST' && req.url.endsWith('/PROJ-001/urls/test'));
    expect(test.request.body.startRule).toMatchObject({ cwd: '04-angular-static-final', port: 4184, source: 'readme' });
    test.flush({
      classification: 'running', summary: 'Ready', recommendedAction: 'None', command: fixture.componentInstance.formCommand(),
      cwd: fixture.componentInstance.formCwd(), url: fixture.componentInstance.formUrl(), configuredPort: 4184,
      processCreated: true, exitCode: null, stdoutTail: '', stderrTail: '', timedOut: false,
      portReachable: true, httpStatus: 200, contentReady: true, checkedAt: '2026-07-13T00:00:00Z',
    });
    expect(fixture.componentInstance.testDiagnostic()?.classification).toBe('running');

    fixture.componentInstance.save();
    const save = http.expectOne(req => req.method === 'POST' && req.url.endsWith('/PROJ-001/urls'));
    expect(save.request.body.startRule).toMatchObject({
      command: 'npm start -- --host 127.0.0.1 --port 4184',
      cwd: '04-angular-static-final', port: 4184, source: 'readme',
    });
    save.flush(workspacesWith([{
      id: 'url-1', label: 'Agent Studio Website', url: 'http://localhost:4184', sortOrder: 0,
      startRule: save.request.body.startRule,
    }])[0].projects[0]);
  });

  it('opens the requested failing URL and prefills its detected setup', () => {
    const { fixture, http } = mount();
    fixture.componentRef.setInput('quickSetup', true);
    TestBed.inject(ProjectUrlRecoveryService).requestQuickSetup('url-1', {
      label: 'Agent Studio Website', url: 'http://localhost:4184',
      command: 'npm start -- --host 127.0.0.1 --port 4184', cwd: '04-angular-static-final',
      port: 4184, source: 'readme',
    });
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([{
      id: 'url-1', label: 'Preview', url: 'http://127.0.0.1:4184', sortOrder: 0,
      startRule: { command: 'broken-command', cwd: 'missing', port: 4184, source: 'manual' },
    }]));
    fixture.detectChanges();

    expect(fixture.componentInstance.editingId()).toBe('url-1');
    expect(fixture.componentInstance.formLabel()).toBe('Preview');
    expect(fixture.componentInstance.formUrl()).toBe('http://127.0.0.1:4184');
    expect(fixture.componentInstance.formCommand()).toBe('npm start -- --host 127.0.0.1 --port 4184');
    expect(fixture.componentInstance.formCwd()).toBe('04-angular-static-final');
    expect(fixture.componentInstance.formSource()).toBe('readme');
  });
});

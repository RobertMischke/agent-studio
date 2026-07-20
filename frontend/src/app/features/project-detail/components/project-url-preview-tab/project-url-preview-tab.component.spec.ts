import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ProjectUrlPreviewTabComponent } from './project-url-preview-tab.component';
import {
  ProjectUrlProbeService,
  type ProjectUrlReadiness,
  type ProjectUrlStatus,
} from '../../../../services/project-url-probe.service';
import type {
  ProjectUrlProcessSnapshot,
  RegistryWorkspaceListItem,
  RegistryProjectUrl,
} from '../../../../models/task.model';

/** Signal-backed probe stub so tests drive running/offline without a real fetch. */
class ProbeStub {
  readonly status = signal<ProjectUrlStatus>('unknown');
  readonly readiness = computed<ProjectUrlReadiness>(() => {
    const status = this.status();
    if (status === 'running') return { kind: 'healthy', statusCode: 200, framePolicy: 'allowed', detail: null, durationMs: 1 };
    if (status === 'failed') return { kind: 'http-error', statusCode: 500, framePolicy: 'allowed', detail: null, durationMs: 1 };
    if (status === 'blocked') return { kind: 'frame-blocked', statusCode: 200, framePolicy: 'blocked', detail: 'X-Frame-Options is DENY.', durationMs: 1 };
    if (status === 'offline') return { kind: 'offline', statusCode: null, framePolicy: 'unknown', detail: null, durationMs: 1 };
    return { kind: 'unknown', statusCode: null, framePolicy: 'unknown', detail: null, durationMs: null };
  });
  statusFor(): ProjectUrlStatus { return this.status(); }
  readinessFor(): ProjectUrlReadiness { return this.readiness(); }
  signalFor() { return this.readiness; }
  refresh(): void { /* no-op */ }
}

function workspacesWith(urls: RegistryProjectUrl[]): RegistryWorkspaceListItem[] {
  return [{
    id: 'ws-1', displayName: 'WS', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-01-01T00:00:00Z',
    projects: [{
      sourceType: 'local-folder', id: 'PROJ-001', displayName: 'Demo', shortCode: 'DEM', workspaceId: 'ws-1',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
      storageLocation: 'c:/tasks', repositoryPath: 'c:/demo', rootPath: null, repositoryUrl: null,
      urls, archived: false, createdAt: '2026-01-01T00:00:00Z',
    }],
  }];
}

function mount(urlId = 'url-1') {
  const probe = new ProbeStub();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: ProjectUrlProbeService, useValue: probe },
    ],
  });
  const fixture = TestBed.createComponent(ProjectUrlPreviewTabComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.componentRef.setInput('urlId', urlId);
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  return { fixture, http, probe };
}

const RUNNING_URL: RegistryProjectUrl = {
  id: 'url-1', label: 'Lab', url: 'http://localhost:4201', sortOrder: 0, startRule: null,
};
const STARTABLE_URL: RegistryProjectUrl = {
  id: 'url-1', label: 'Website', url: 'http://localhost:4202', sortOrder: 0,
  startRule: { command: 'npm run website', cwd: null, port: null, source: 'manual' },
};

function processSnapshot(
  state: ProjectUrlProcessSnapshot['state'] = 'running',
  output = ['ready in 412 ms'],
): ProjectUrlProcessSnapshot {
  return {
    started: true,
    projectId: 'PROJ-001',
    urlId: 'url-1',
    command: 'npm run website',
    cwd: 'c:/demo',
    state,
    processId: 42,
    startedAtUtc: '2026-07-13T20:00:00Z',
    finishedAtUtc: state === 'running' ? null : '2026-07-13T20:01:00Z',
    exitCode: state === 'running' ? null : 0,
    output,
  };
}

describe('ProjectUrlPreviewTabComponent', () => {
  afterEach(() => vi.useRealTimers());

  it('mounts the sandboxed iframe once the URL resolves (running / unknown)', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('running');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([RUNNING_URL]));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    const frame = el.querySelector<HTMLIFrameElement>('[data-testid="url-preview-frame"]');
    expect(frame).toBeTruthy();
    // Sandbox present, but no top-navigation escape.
    expect(frame?.getAttribute('sandbox')).toContain('allow-scripts');
    expect(frame?.getAttribute('sandbox')).not.toContain('allow-top-navigation');
    const addr = el.querySelector('[data-testid="url-preview-addr-input"]') as HTMLInputElement;
    expect(addr?.value).toBe('http://localhost:4201');
  });

  it('navigates the embedded frame to a typed URL without touching the registry', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    const input = fixture.nativeElement.querySelector('[data-testid="url-preview-addr-input"]') as HTMLInputElement;
    input.value = 'http://localhost:4202/health';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    fixture.detectChanges();

    const frame = fixture.nativeElement.querySelector('[data-testid="url-preview-frame"]') as HTMLIFrameElement;
    expect(frame?.getAttribute('src')).toBe('http://localhost:4202/health');
    expect(fixture.componentInstance.urlRecord()?.url).toBe('http://localhost:4202');
    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-status"]')?.getAttribute('data-status'))
      .toBe('custom');
  });

  it('shows a contained offline panel with project context and a Start button', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    const offline = el.querySelector('[data-testid="url-preview-offline"]');
    expect(offline).toBeTruthy();
    expect(offline?.querySelector('.url-preview__state-card')).toBeTruthy();
    expect(offline?.textContent).toContain('Demo');
    expect(offline?.textContent).toContain('Website');
    expect(offline?.textContent).toContain('npm run website');
    expect(offline?.textContent).toContain('c:/demo');
    expect(el.querySelector('[data-testid="url-preview-start"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="url-preview-frame"]')).toBeFalsy();
  });

  it('does not mount an HTTP 500 document and shows status plus recovery actions', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('failed');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    const failure: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-http-error"]');
    expect(failure?.textContent).toContain('HTTP 500');
    expect(failure?.querySelector('[data-testid="url-preview-failure-reload"]')).toBeTruthy();
    expect(failure?.querySelector('[data-testid="url-preview-restart"]')).toBeTruthy();
    expect(failure?.querySelector('[data-testid="url-preview-failure-open-external"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-frame"]')).toBeFalsy();
    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-status"]')?.getAttribute('data-status')).toBe('failed');
  });

  it('shows a removed state when the URL is no longer on the project', () => {
    const { fixture, http } = mount('url-gone');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([RUNNING_URL]));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="url-preview-not-found"]')).toBeTruthy();
  });

  it('opens settings for the current embed without leaving the preview', () => {
    const { fixture, http } = mount();
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([RUNNING_URL]));
    fixture.detectChanges();

    fixture.componentInstance.onSettings();
    fixture.detectChanges();

    expect(fixture.componentInstance.settingsOpen()).toBe(true);
    expect(document.querySelector('[data-testid="url-preview-settings-dialog"]')).toBeTruthy();
  });

  it('keeps the pending state visible until the started URL is reachable', () => {
    vi.useFakeTimers();
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    fixture.componentInstance.start();
    fixture.detectChanges();
    // While starting, the progress row is the single pending indicator; the
    // Start button leaves the action row instead of duplicating it as
    // a disabled "Starting…" state.
    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-start"]')).toBeFalsy();
    const starting: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-starting"]');
    expect(starting?.textContent).toContain('accept connections');
    expect(starting?.textContent).toContain('http://localhost:4202');
    const post = http.expectOne(req => req.method === 'POST' && req.url.endsWith('/PROJ-001/urls/url-1/start'));
    expect(post.request.method).toBe('POST');
    post.flush(processSnapshot());

    probe.status.set('running');
    vi.advanceTimersByTime(1_000);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-frame"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-offline"]')).toBeFalsy();
  });

  it('renders backend start errors with Retry and Edit settings actions', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    fixture.componentInstance.start();
    http.expectOne(req => req.method === 'POST').flush({
      error: 'Working directory does not exist: c:/missing',
      command: 'npm run website',
      cwd: 'c:/missing',
    }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    const failed: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-start-failed"]');
    expect(failed?.textContent).toContain('Working directory does not exist');
    expect(failed?.querySelector('[data-testid="url-preview-retry"]')).toBeTruthy();
    // Command and cwd live in the console below the card (the card drops its
    // duplicate copy while the console is visible).
    expect(failed?.querySelector('[data-testid="url-preview-start-details"]')).toBeFalsy();
    const consoleEl: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-process-console"]');
    expect(consoleEl?.textContent).toContain('npm run website');
    expect(consoleEl?.textContent).toContain('c:/missing');

    (failed?.querySelector('[data-testid="url-preview-edit-settings"]') as HTMLButtonElement).click();
    expect(fixture.componentInstance.settingsOpen()).toBe(true);
  });

  it('turns an accepted start that never becomes reachable into an actionable failure', () => {
    vi.useFakeTimers();
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    fixture.componentInstance.start();
    http.expectOne(req => req.method === 'POST').flush(processSnapshot());
    vi.advanceTimersByTime(25_000);
    fixture.detectChanges();

    const failed: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-start-failed"]');
    expect(failed?.textContent).toContain('did not become reachable');
    const consoleEl: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-process-console"]');
    expect(consoleEl?.textContent).toContain('c:/demo');
  });

  it('shows bounded live output in place and stops the owned process explicitly', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    http.expectOne(req => req.method === 'GET' && req.url.endsWith('/process'))
      .flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    fixture.componentInstance.start();
    http.expectOne(req => req.method === 'POST' && req.url.endsWith('/start'))
      .flush(processSnapshot('running', ['vite --port 4202', 'ready in 412 ms']));
    fixture.detectChanges();

    const console: HTMLElement | null = fixture.nativeElement.querySelector('[data-testid="url-preview-process-console"]');
    expect(console?.textContent).toContain('npm run website');
    expect(console?.querySelector('[data-testid="url-preview-process-output"]')?.textContent)
      .toContain('ready in 412 ms');

    fixture.componentInstance.stop();
    http.expectOne(req => req.method === 'DELETE' && req.url.endsWith('/process'))
      .flush(processSnapshot('stopped', ['[studio] Process stopped by operator.']));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="url-preview-process-status"]')?.textContent)
      .toContain('Stopped');
  });

  it('offers start, console, stop, settings, and external actions in the embed menu', () => {
    const { fixture, http } = mount();
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    http.expectOne(req => req.method === 'GET' && req.url.endsWith('/process')).flush(processSnapshot());
    fixture.detectChanges();

    const rows = fixture.componentInstance.menuItems().filter(item => item.kind === 'row');
    expect(rows.map(item => item.id)).toEqual(['start', 'console', 'stop', 'settings', 'external']);
    expect(rows.find(item => item.id === 'stop')?.disabled).toBe(false);
  });

  it('saves URL, command, cwd, and port from the per-embed settings dialog', () => {
    const { fixture, http } = mount();
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    fixture.componentInstance.saveSettings({
      label: 'Website',
      url: 'http://localhost:4300',
      startRule: { command: 'npm run preview', cwd: 'c:/demo/web', port: 4300, source: 'manual' },
    });
    const request = http.expectOne(req => req.method === 'PUT' && req.url.endsWith('/PROJ-001/urls/url-1'));
    expect(request.request.body).toMatchObject({
      url: 'http://localhost:4300',
      startRule: { command: 'npm run preview', cwd: 'c:/demo/web', port: 4300 },
    });
    request.flush(workspacesWith([STARTABLE_URL])[0].projects[0]);
    fixture.detectChanges();

    expect(fixture.componentInstance.urlRecord()?.url).toBe('http://localhost:4300');
    expect(fixture.componentInstance.urlRecord()?.startRule?.cwd).toBe('c:/demo/web');
  });
});

import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { ProjectUrlPreviewTabComponent } from './project-url-preview-tab.component';
import { ProjectUrlProbeService, type ProjectUrlStatus } from '../../../../services/project-url-probe.service';
import type { RegistryWorkspaceListItem, RegistryProjectUrl } from '../../../../models/task.model';

/** Signal-backed probe stub so tests drive running/offline without a real fetch. */
class ProbeStub {
  readonly status = signal<ProjectUrlStatus>('unknown');
  statusFor(): ProjectUrlStatus { return this.status(); }
  signalFor() { return this.status; }
  refresh(): void { /* no-op */ }
}

function workspacesWith(urls: RegistryProjectUrl[]): RegistryWorkspaceListItem[] {
  return [{
    id: 'ws-1', displayName: 'WS', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-01-01T00:00:00Z',
    projects: [{
      sourceType: 'local-folder', id: 'PROJ-001', displayName: 'Demo', shortCode: 'DEM', workspaceId: 'ws-1',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
      storageLocation: 'c:/demo', urls, archived: false, createdAt: '2026-01-01T00:00:00Z',
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

describe('ProjectUrlPreviewTabComponent', () => {
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
    expect(el.querySelector('[data-testid="url-preview-addr"]')?.textContent).toContain('localhost:4201');
  });

  it('shows the offline card with a Start button when the server is down', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="url-preview-offline"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="url-preview-start"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="url-preview-frame"]')).toBeFalsy();
  });

  it('shows a removed state when the URL is no longer on the project', () => {
    const { fixture, http } = mount('url-gone');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([RUNNING_URL]));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="url-preview-not-found"]')).toBeTruthy();
  });

  it('emits openSettings with the project name for the settings deep link', () => {
    const { fixture, http } = mount();
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([RUNNING_URL]));
    fixture.detectChanges();

    let emitted: { projectName: string } | null = null;
    fixture.componentInstance.openSettings.subscribe(e => (emitted = e));
    fixture.componentInstance.onSettings();
    expect(emitted).toEqual({ projectName: 'Demo' });
  });

  it('starting the dev server posts to the URL start endpoint', () => {
    const { fixture, http, probe } = mount();
    probe.status.set('offline');
    http.expectOne(req => req.url.endsWith('/workspaces')).flush(workspacesWith([STARTABLE_URL]));
    fixture.detectChanges();

    fixture.componentInstance.start();
    const post = http.expectOne(req => req.method === 'POST' && req.url.endsWith('/PROJ-001/urls/url-1/start'));
    expect(post.request.method).toBe('POST');
    post.flush({ started: true, urlId: 'url-1' });
  });
});

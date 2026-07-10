import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { ProjectUrlsPanelComponent } from './project-urls-panel.component';
import type { RegistryWorkspaceListItem } from '../../../../models/task.model';

function workspacesWith(urls: RegistryWorkspaceListItem['projects'][number]['urls']): RegistryWorkspaceListItem[] {
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
});

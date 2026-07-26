import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectCliEnvironmentSectionComponent } from './project-cli-environment-section';

describe('ProjectCliEnvironmentSectionComponent', () => {
  it('renders CLI path, version, and project session from the usage report', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectCliEnvironmentSectionComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectCliEnvironmentSectionComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    fixture.componentRef.setInput('paths', {
      path: 'C:/Projects/demo',
      rootPath: 'C:/Projects/demo',
      repositoryPath: 'C:/Projects/demo',
    });
    fixture.componentRef.setInput('modeRows', [
      { cliType: 'claude', mode: 'workspace-write', source: 'project' },
    ]);
    fixture.componentRef.setInput('contextModeRows', [
      { cliType: 'claude', mode: 'shared', source: 'project', supported: true },
    ]);
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/cli/usage').flush({
      at: '2026-06-22T10:00:00Z',
      sections: [{
        cliType: 'claude',
        available: true,
        version: 'claude 1.2.3',
        path: 'C:/tools/claude.cmd',
        error: null,
        projects: [{
          projectName: 'Demo Project',
          rootPath: 'C:/Projects/demo',
          sessions: [{
            id: '1234567890abcdef',
            label: 'feature pass',
            updatedAt: '2026-06-22T09:30:00Z',
            cwd: 'C:/Projects/demo',
            lastUsage: null,
            isProjectDefault: false,
            linkedJob: null,
          }],
        }],
      }],
    });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('CLI environment');
    expect(text).toContain('1 / 3 CLIs ready');
    expect(text).toContain('1 project session found');
    expect(text).toContain('claude 1.2.3');
    expect(text).toContain('C:/tools/claude.cmd');
    expect(text).toContain('feature pass');
    expect(text).toContain('Workspace-Write');
    expect(text).toContain('project override');
    expect(text).toContain('Shared');
    http.verify();
  });

  it('renders compact onboarding tiles without the detailed CLI cards', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectCliEnvironmentSectionComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectCliEnvironmentSectionComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    fixture.componentRef.setInput('presentation', 'onboarding');
    fixture.componentRef.setInput('paths', {
      path: 'C:/Projects/demo',
      rootPath: 'C:/Projects/demo',
      repositoryPath: 'C:/Projects/demo',
    });
    fixture.componentRef.setInput('modeRows', [
      { cliType: 'claude', mode: 'workspace-write', source: 'project override' },
      { cliType: 'codex', mode: 'yolo', source: 'platform default' },
    ]);
    fixture.componentRef.setInput('contextModeRows', [
      { cliType: 'claude', mode: 'clean', source: 'platform default', supported: true },
      { cliType: 'codex', mode: 'clean', source: 'platform default', supported: true },
    ]);
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/cli/usage').flush({
      at: '2026-06-22T10:00:00Z',
      sections: [
        {
          cliType: 'claude',
          available: true,
          version: 'claude 1.2.3',
          path: 'C:/tools/claude.cmd',
          error: null,
          projects: [{
            projectName: 'Demo Project',
            rootPath: 'C:/Projects/demo',
            sessions: [{
              id: '1234567890abcdef',
              label: 'feature pass',
              updatedAt: '2026-06-22T09:30:00Z',
              cwd: 'C:/Projects/demo',
              lastUsage: null,
              isProjectDefault: false,
              linkedJob: null,
            }],
          }],
        },
        { cliType: 'codex', available: true, version: 'codex 1.0.0', path: 'C:/tools/codex.cmd', error: null, projects: [] },
      ],
    });
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-cli-onboarding-status"]')?.textContent).toContain('Onboarding status');
    expect(host.querySelector('[data-testid="project-cli-onboarding-tile-cli-ready"]')?.textContent).toContain('2 / 3');
    expect(host.querySelector('[data-testid="project-cli-onboarding-tile-clean-context"]')?.textContent).toContain('2 / 2');
    expect(host.querySelector('[data-testid="project-cli-onboarding-tile-project-sessions"]')?.textContent).toContain('feature pass');
    expect(host.querySelector('[data-testid="project-cli-onboarding-tile-overrides"]')?.textContent).toContain('1');
    expect(host.querySelector('[data-testid="project-detail-cli-environment"]')).toBeNull();
    http.verify();
  });
});

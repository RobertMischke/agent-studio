import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { ProjectStyleGuidesPanelComponent } from './project-style-guides-panel';

describe('ProjectStyleGuidesPanelComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectStyleGuidesPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  it('renders loading, snapshot, match reasons and emits the selected Wiki path', () => {
    const fixture = TestBed.createComponent(ProjectStyleGuidesPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="project-wiki-style-guides-loading"]')).not.toBeNull();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/Demo/style-guides').flush({
      projectKey: 'PROJ-0042',
      projectDisplayName: 'Demo',
      technologies: [{ key: 'angular', displayLabel: 'Angular' }],
      guides: [{
        id: 'angular-components',
        title: 'Angular component guide',
        relPath: 'quality/angular-components.md',
        summary: 'Rendering and token rules.',
        promptSummary: 'Use OnPush.',
        version: '1',
        appliesTo: { projects: ['*'], technologies: ['angular'], taskAreas: ['frontend'] },
        match: {
          projectWildcard: true,
          projectSelector: '*',
          technologyWildcard: false,
          technologies: [{ key: 'angular', displayLabel: 'Angular' }],
        },
      }],
      warnings: [],
      snapshotId: '0123456789abcdef',
      capturedAtUtc: '2026-07-14T08:00:00Z',
      refreshAfterUtc: '2026-07-14T08:05:00Z',
    });
    const selected: string[] = [];
    fixture.componentInstance.openGuide.subscribe(path => selected.push(path));
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('Angular');
    expect(host.textContent).toContain('Snapshot 01234567');
    expect(host.textContent).toContain('Matches all projects');
    expect(host.textContent).toContain('Technology: Angular');
    expect(host.textContent).toContain('Prompt context · v1');
    host.querySelector<HTMLButtonElement>('[data-testid="project-wiki-style-guide-angular-components"]')!.click();

    expect(selected).toEqual(['quality/angular-components.md']);
    TestBed.inject(HttpTestingController).verify();
  });

  it('shows an explicit generic error state', () => {
    const fixture = TestBed.createComponent(ProjectStyleGuidesPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/Demo/style-guides').flush(
      { detail: 'C:\\secret\\repository' },
      { status: 500, statusText: 'Server Error' },
    );
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-wiki-style-guides-loading"]')).toBeNull();
    expect(host.querySelector('[data-testid="project-wiki-style-guides-error"]')?.textContent)
      .toContain('Engineering style guides could not be loaded.');
    expect(host.textContent).not.toContain('secret');
    TestBed.inject(HttpTestingController).verify();
  });
});

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

  it('renders applicable technologies and emits the selected Wiki path', () => {
    const fixture = TestBed.createComponent(ProjectStyleGuidesPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/Demo/style-guides').flush({
      projectName: 'Demo',
      repositoryRoot: '/repo',
      technologies: ['angular', 'typescript'],
      guides: [{
        id: 'angular-components',
        title: 'Angular component guide',
        relPath: 'quality/angular-components.md',
        summary: 'Rendering and token rules.',
        promptSummary: 'Use OnPush.',
        version: '1',
        appliesTo: { projects: ['*'], technologies: ['angular'], taskAreas: ['frontend'] },
      }],
      warnings: [],
    });
    const selected: string[] = [];
    fixture.componentInstance.openGuide.subscribe(path => selected.push(path));
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('angular');
    expect(host.textContent).toContain('Prompt context · v1');
    host.querySelector<HTMLButtonElement>('[data-testid="project-wiki-style-guide-angular-components"]')!.click();

    expect(selected).toEqual(['quality/angular-components.md']);
    TestBed.inject(HttpTestingController).verify();
  });
});

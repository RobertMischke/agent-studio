import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectWikiSectionComponent } from './project-wiki-section';
import type { WikiOverview } from '../../../../models/project-docs.model';

const OVERVIEW: WikiOverview = {
  projectName: 'Demo',
  baseDir: '/repo/docs',
  exists: true,
  files: [
    { name: 'README.md', relPath: 'README.md', title: 'Docs index', updatedAt: '2026-06-01T00:00:00Z', size: 10 },
    { name: 'overview.md', relPath: 'concepts/overview.md', title: 'Concept overview', updatedAt: '2026-06-02T00:00:00Z', size: 20 },
  ],
};

async function setup() {
  await TestBed.configureTestingModule({
    imports: [ProjectWikiSectionComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(ProjectWikiSectionComponent);
  const http = TestBed.inject(HttpTestingController);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.detectChanges();

  http.expectOne('/api/projects/Demo/wiki').flush(OVERVIEW);
  fixture.detectChanges();
  return { fixture, http };
}

describe('ProjectWikiSectionComponent', () => {
  it('renders the docs tree grouped by folder', async () => {
    const { fixture, http } = await setup();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Docs index');
    expect(text).toContain('Concept overview');
    expect(text).toContain('concepts');
    expect(text).toContain('2 docs');
    http.verify();
  });

  it('loads a document into the viewer on click', async () => {
    const { fixture, http } = await setup();
    const btn = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/overview.md"]');
    expect(btn).toBeTruthy();
    btn!.click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n\nBody text.' });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Hello wiki');
    http.verify();
  });

  it('filters the tree by needle', async () => {
    const { fixture, http } = await setup();
    fixture.componentInstance.filter.set('concept');
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Concept overview');
    expect(text).not.toContain('Docs index');
    http.verify();
  });
});

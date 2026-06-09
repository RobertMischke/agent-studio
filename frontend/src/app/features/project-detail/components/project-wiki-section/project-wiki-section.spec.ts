import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectWikiSectionComponent } from './project-wiki-section';
import type { WikiFileHistory, WikiOrganization, WikiOverview } from '../../../../models/project-docs.model';

const OVERVIEW: WikiOverview = {
  projectName: 'Demo',
  baseDir: '/repo/docs',
  exists: true,
  files: [
    { name: 'README.md', relPath: 'README.md', title: 'Docs index', updatedAt: '2026-06-01T00:00:00Z', size: 10 },
    { name: 'overview.md', relPath: 'concepts/overview.md', title: 'Concept overview', updatedAt: '2026-06-02T00:00:00Z', size: 20 },
  ],
};

const EMPTY_ORG: WikiOrganization = { version: 1, nodes: [] };

async function setup(org: WikiOrganization = EMPTY_ORG) {
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
  http.expectOne('/api/projects/Demo/wiki/organization').flush(org);
  fixture.detectChanges();
  return { fixture, http };
}

const el = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

describe('ProjectWikiSectionComponent', () => {
  it('renders every doc under the Ungrouped bucket when no manifest exists', async () => {
    const { fixture, http } = await setup();
    const text = el(fixture).textContent ?? '';
    expect(text).toContain('Docs index');
    expect(text).toContain('Concept overview');
    expect(text).toContain('Ungrouped');
    expect(text).toContain('2 docs');
    http.verify();
  });

  it('groups a pinned doc under its theme and leaves the rest Ungrouped', async () => {
    const { fixture, http } = await setup({
      version: 1,
      nodes: [
        { id: 'g1', type: 'group', title: 'Concepts', relPath: null, parentId: null, order: 0 },
        { id: 'doc:concepts/overview.md', type: 'doc', title: null, relPath: 'concepts/overview.md', parentId: 'g1', order: 0 },
      ],
    });
    const groupRow = el(fixture).querySelector('[data-testid="project-wiki-node-g1"]');
    expect(groupRow?.textContent).toContain('Concepts');
    expect(el(fixture).textContent).toContain('Ungrouped'); // README.md still loose
    http.verify();
  });

  it('loads a document and its history on click', async () => {
    const { fixture, http } = await setup();
    const btn = el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/overview.md"]');
    expect(btn).toBeTruthy();
    btn!.click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n\nBody text.' });
    const history: WikiFileHistory = {
      relPath: 'concepts/overview.md',
      model: 'Claude Opus 4.8',
      metadata: {
        model: 'Claude Opus 4.8', updatedAt: '2026-06-02T00:00:00Z', reason: 'distilled',
        taskKey: null, status: null, runCount: null, hasFrontmatter: true,
      },
      commits: [
        { sha: 'abc', shortSha: 'abc1234', authorDateUtc: '2026-06-02T00:00:00Z', author: 'bot', subject: 'update', filesChanged: 1, added: 3, removed: 1 },
      ],
    };
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(history);
    fixture.detectChanges();

    expect(el(fixture).textContent).toContain('Hello wiki');

    // Switch to the History tab and assert provenance + commit render.
    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-tab-history"]')!.click();
    fixture.detectChanges();
    const text = el(fixture).textContent ?? '';
    expect(text).toContain('Claude Opus 4.8');
    expect(text).toContain('abc1234');
    http.verify();
  });

  it('filters the tree by needle', async () => {
    const { fixture, http } = await setup();
    fixture.componentInstance.filter.set('concept');
    fixture.detectChanges();
    const text = el(fixture).textContent ?? '';
    expect(text).toContain('Concept overview');
    expect(text).not.toContain('Docs index');
    http.verify();
  });

  it('creates a group and persists the manifest', async () => {
    const { fixture, http } = await setup();
    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-new-group"]')!.click();
    fixture.detectChanges();

    const put = http.expectOne(req =>
      req.method === 'PUT' && req.url === '/api/projects/Demo/wiki/organization');
    const body = put.request.body as WikiOrganization;
    expect(body.nodes.some(n => n.type === 'group' && n.title === 'New group')).toBe(true);
    put.flush(body);
    fixture.detectChanges();

    // A rename input opens for the freshly created group.
    expect(el(fixture).querySelector('.pwiki__rename-input')).toBeTruthy();
    http.verify();
  });
});

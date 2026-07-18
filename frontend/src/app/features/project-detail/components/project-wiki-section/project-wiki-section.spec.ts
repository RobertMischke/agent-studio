import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { vi } from 'vitest';
import { ProjectWikiSectionComponent } from './project-wiki-section';
import type {
  ProjectStyleGuideCatalogue,
  WikiFileHistory,
  WikiFolderOverview,
  WikiHome,
  WikiPulse,
  WikiSearchResponse,
  WikiTree,
} from '../../../../models/project-docs.model';

const TREE: WikiTree = {
  projectName: 'Demo',
  baseDir: '/repo/docs',
  exists: true,
  root: [
    {
      name: 'concepts', title: 'concepts', relPath: 'concepts', type: 'folder', children: [
        {
          name: 'overview.md',
          title: 'Concept overview',
          relPath: 'concepts/overview.md',
          type: 'md',
          children: [],
          metadata: {
            documentMode: 'documentation',
            temporalState: 'present',
            implementationState: 'implemented',
            driftGrade: 'B',
            hasDrift: true,
            driftScore: 0.24,
            quality: 'medium',
            duplicateSuspected: false,
            duplicateGroupSize: 1,
            reportPath: 'concepts/overview.md.report.html',
            summary: 'Light sample drift.',
            companionPath: 'concepts/overview.md.meta.json',
            sourceChangedSinceReview: false,
            findingsCount: 2,
          },
          classification: {
            status: 'ueberholt',
            supersededBy: 'concepts/new-overview.md',
            type: 'konzept',
            analyzedAt: '2026-07-18',
          },
        },
        { name: 'page.html', title: 'HTML page', relPath: 'concepts/page.html', type: 'html', children: [] },
        { name: 'page.metadata.json', title: 'Page metadata', relPath: 'concepts/page.metadata.json', type: 'json', children: [] },
      ],
    },
    { name: 'README.md', title: 'Docs index', relPath: 'README.md', type: 'md', children: [] },
  ],
};

/** Three top-level categories (beta with a nested folder) for reorder tests. */
const REORDER_TREE: WikiTree = {
  projectName: 'Demo',
  baseDir: '/repo/docs',
  exists: true,
  root: [
    {
      name: 'alpha', title: 'alpha', relPath: 'alpha', type: 'folder', children: [
        { name: 'a.md', title: 'Alpha page', relPath: 'alpha/a.md', type: 'md', children: [] },
      ],
    },
    {
      name: 'beta', title: 'beta', relPath: 'beta', type: 'folder', children: [
        {
          name: 'inner', title: 'inner', relPath: 'beta/inner', type: 'folder', children: [
            { name: 'i.md', title: 'Inner page', relPath: 'beta/inner/i.md', type: 'md', children: [] },
          ],
        },
        { name: 'b.md', title: 'Beta page', relPath: 'beta/b.md', type: 'md', children: [] },
      ],
    },
    {
      name: 'gamma', title: 'gamma', relPath: 'gamma', type: 'folder', children: [
        { name: 'g.md', title: 'Gamma page', relPath: 'gamma/g.md', type: 'md', children: [] },
      ],
    },
  ],
};

const PULSE: WikiPulse = {
  projectName: 'Demo',
  baseDir: '/repo/docs',
  exists: true,
  generatedAtUtc: '2026-07-10T09:00:00Z',
  feed: {
    available: true,
    reason: null,
    items: [
      {
        relPath: 'concepts/overview.md',
        title: 'Concept overview',
        author: 'Alice',
        authorDateUtc: '2026-07-10T08:00:00Z',
        sha: 'abc1234',
        shortSha: 'abc1234',
        subject: 'AGT-2014 refine overview',
        frameAreaSlug: null,
        frameAreaTitle: null,
        taskKey: 'AGT-2014',
      },
    ],
  },
  inbox: {
    available: true,
    reason: null,
    count: 1,
    items: [
      {
        relPath: 'stray.md',
        title: 'Stray note',
        type: 'md',
        reason: 'Loose page at the wiki root - not filed under a category.',
      },
    ],
  },
  drift: {
    available: true,
    reason: null,
    overallGrade: 'Aging',
    areas: [
      { slug: '10-current-development-state', title: 'Current Development State', grade: 'Aging', pageCount: 1, gradedPageCount: 1, worstCommitCount: 12, freshCount: 0, agingCount: 1, staleCount: 0 },
      { slug: '20-development-signals', title: 'Development Signals', grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 },
      { slug: '30-system-knowledge', title: 'System Knowledge', grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 },
      { slug: '40-decision-log', title: 'Decision Log', grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 },
      { slug: '50-workstream-log', title: 'Workstream Log', grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 },
    ],
    counts: { fresh: 0, aging: 1, stale: 0, graded: 1 },
  },
  critical: { available: true, reason: 'No pages have been graded yet.', count: 0, overallGrade: 'none', items: [] },
};

const STYLE_GUIDES: ProjectStyleGuideCatalogue = {
  projectKey: 'PROJ-0042',
  projectDisplayName: 'Demo',
  technologies: [
    { key: 'angular', displayLabel: 'Angular' },
    { key: 'dotnet', displayLabel: '.NET' },
  ],
  guides: [
    {
      id: 'angular-components',
      title: 'Angular component guide',
      relPath: 'quality/angular-components.md',
      summary: 'Rendering, identity, and token rules for Angular UI work.',
      promptSummary: 'Use OnPush and stable tracking.',
      version: '1',
      appliesTo: { projects: ['*'], technologies: ['angular'], taskAreas: ['frontend'] },
      match: {
        projectWildcard: true,
        projectSelector: '*',
        technologyWildcard: false,
        technologies: [{ key: 'angular', displayLabel: 'Angular' }],
      },
    },
    {
      id: 'dotnet-backend',
      title: '.NET backend guide',
      relPath: 'quality/dotnet-backend.md',
      summary: 'Feature ownership and pure policy rules for backend work.',
      promptSummary: 'Use pure policy tests.',
      version: '1',
      appliesTo: { projects: ['*'], technologies: ['dotnet'], taskAreas: ['backend'] },
      match: {
        projectWildcard: true,
        projectSelector: '*',
        technologyWildcard: false,
        technologies: [{ key: 'dotnet', displayLabel: '.NET' }],
      },
    },
  ],
  warnings: [],
  snapshotId: '0123456789abcdef',
  capturedAtUtc: '2026-07-14T08:00:00Z',
  refreshAfterUtc: '2026-07-14T08:05:00Z',
};

const HOME: WikiHome = {
  sections: [
    {
      title: 'Start',
      links: [
        { relPath: 'concepts/overview.md', label: 'Konzept-Überblick', note: 'Der Einstieg', exists: true },
        { relPath: 'workbench/overview.html', label: 'Workbench', note: null, exists: true },
        { relPath: 'missing/gone.md', label: 'Verschollen', note: 'alte Seite', exists: false },
      ],
    },
    {
      title: 'Betrieb',
      links: [{ relPath: 'ops/runbook.md', label: 'Runbook', note: null, exists: true }],
    },
  ],
};

async function setup(tree: WikiTree = TREE, pulse: WikiPulse = PULSE) {
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

  http.expectOne('/api/projects/Demo/wiki/tree').flush(tree);
  flushWikiPulse(http, pulse);
  flushGradingContext(http);
  fixture.detectChanges();
  flushStyleGuidesIfRendered(http);
  flushWikiHomeIfRendered(http);
  fixture.detectChanges();
  return { fixture, http };
}

/**
 * The grading trigger seeds its maintenance-model default and the current run
 * status once per project on open (AGT-2051). Both fire from the same effect as
 * the tree/pulse load, so the fake backend must answer them or verify() trips.
 */
function flushGradingContext(http: HttpTestingController): void {
  http.expectOne('/api/cli/maintenance-model')
    .flush({ cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null });
  http.expectOne(r => r.url.includes('/wiki/grading/status')).flush({ status: null });
}

/**
 * refresh() fetches the git-backed Pulse landing view alongside the tree. Every
 * refresh (initial load and post-mutation re-read) issues it, so the fake
 * backend must answer it or verify() trips on the dangling request.
 */
function flushWikiPulse(http: HttpTestingController, pulse: WikiPulse = PULSE): void {
  http.expectOne(r => r.url.includes('/wiki/pulse')).flush(pulse);
}

/** The style-guide panel is absent when persisted state opens a document directly. */
function flushStyleGuidesIfRendered(http: HttpTestingController): void {
  const requests = http.match('/api/projects/Demo/style-guides');
  expect(requests.length).toBeLessThanOrEqual(1);
  requests[0]?.flush(STYLE_GUIDES);
}

/**
 * The curated "Einstiege" block self-fetches /wiki/home whenever the landing
 * view mounts; like the style-guide panel it is absent when persisted state
 * opens a document directly, so the request is flushed only when present.
 */
function flushWikiHomeIfRendered(http: HttpTestingController, home: WikiHome = HOME): void {
  const requests = http.match('/api/projects/Demo/wiki/home');
  expect(requests.length).toBeLessThanOrEqual(1);
  requests[0]?.flush(home);
}

const el = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

function expandConcepts(fixture: { componentInstance: ProjectWikiSectionComponent; detectChanges: () => void }): void {
  fixture.componentInstance.toggleExpand('concepts');
  fixture.detectChanges();
}

function wikiStorageKey(projectName = 'Demo'): string {
  return `atp.projectWiki.v1.${encodeURIComponent(projectName)}`;
}

function clearWikiStorage(): void {
  for (const key of Object.keys(localStorage)) {
    if (key.startsWith('atp.projectWiki.v1.') || key.startsWith('atp.projectWikiStars.v1.')) {
      localStorage.removeItem(key);
    }
  }
}

/** Minimal DataTransfer backed by a Map so getData/setData round-trip in jsdom. */
function makeDataTransfer(): DataTransfer {
  const store = new Map<string, string>();
  return {
    dropEffect: 'none',
    effectAllowed: 'all',
    setData(type: string, val: string) { store.set(type, val); },
    getData(type: string) { return store.get(type) ?? ''; },
  } as unknown as DataTransfer;
}

/** Dispatches a drag event carrying a shared DataTransfer (jsdom has no DragEvent). */
function fireDrag(target: Element, type: string, dt: DataTransfer): void {
  const ev = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperty(ev, 'dataTransfer', { value: dt, configurable: true });
  target.dispatchEvent(ev);
}

function fakePointer(clientX: number, pointerId = 1): PointerEvent {
  const noop = () => undefined;
  return {
    clientX,
    pointerId,
    preventDefault: noop,
    currentTarget: {
      setPointerCapture: noop,
      releasePointerCapture: noop,
    },
  } as unknown as PointerEvent;
}

const HISTORY: WikiFileHistory = {
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

describe('ProjectWikiSectionComponent', () => {
  beforeEach(() => {
    clearWikiStorage();
  });

  it('renders the physical folder tree with folders collapsed by default and md/html/json files when expanded', async () => {
    const { fixture, http } = await setup();
    let text = el(fixture).querySelector('[data-testid="project-wiki-tree"]')?.textContent ?? '';
    expect(text).toContain('concepts');
    expect(text).not.toContain('Concept overview');
    expect(text).toContain('Docs index');
    expect(el(fixture).textContent).toContain('4 pages'); // overview.md + page.html + page.metadata.json + README.md
    expect(el(fixture).querySelector('[data-testid="project-wiki-node-concepts"]')?.getAttribute('aria-expanded'))
      .toBe('false');

    expandConcepts(fixture);
    text = el(fixture).querySelector('[data-testid="project-wiki-tree"]')?.textContent ?? '';
    expect(text).toContain('Concept overview');
    expect(text).toContain('Page metadata');
    // The nav exposes the physical root path, compact per-document ratings, and subtle file type icons.
    expect(el(fixture).querySelector('[data-testid="project-wiki-root-path"]')!.textContent)
      .toContain('/repo/docs');
    const ratings = el(fixture).querySelector('[data-testid="project-wiki-ratings-concepts/overview.md"]');
    expect(ratings, 'document ratings').toBeTruthy();
    expect(
      ratings!.querySelector('[data-testid="project-wiki-metric-concepts/overview.md-drift"]')?.getAttribute('aria-label')
    ).toBe('Drift B');
    expect(
      ratings!.querySelector('[data-testid="project-wiki-metric-concepts/overview.md-direction"]')?.getAttribute('aria-label')
    ).toBe('Direction Current');
    expect(ratings!.textContent).toContain('B');
    expect(ratings!.textContent).toContain('Now');
    expect(ratings!.textContent).not.toContain('D:');
    expect(ratings!.textContent).not.toContain('Dir:');
    expect(ratings!.textContent).not.toContain('DriftB');
    expect(ratings!.textContent).not.toContain('DirectionCurrent');
    expect(ratings!.textContent).not.toContain('S 76');
    expect(ratings!.textContent).not.toContain('Q B');
    expect(
      el(fixture)
        .querySelector('[data-testid="project-wiki-metric-concepts/page.html-unscored"]')
        ?.getAttribute('aria-label')
    ).toBe('Metadata unscored');
    expect(el(fixture).querySelector('[data-file-type="html"]')).toBeTruthy();
    expect(el(fixture).querySelector('[data-file-type="json"]')).toBeTruthy();
    http.verify();
  });

  it('renders classification chips on page rows: status + compact type code, nothing when unclassified', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);

    const strip = el(fixture).querySelector('[data-testid="project-wiki-class-concepts/overview.md"]');
    expect(strip, 'classification strip').toBeTruthy();
    const status = strip!.querySelector('[data-testid="project-wiki-class-concepts/overview.md-status"]');
    expect(status?.textContent?.trim()).toBe('überholt');
    expect(status?.getAttribute('data-tone')).toBe('superseded');
    const type = strip!.querySelector('[data-testid="project-wiki-class-concepts/overview.md-type"]');
    expect(type?.textContent?.trim()).toBe('KON');
    expect(type?.getAttribute('data-tone')).toBe('muted');

    // The analysis date lives in the tooltips, not as visible column text.
    expect(strip!.textContent).not.toContain('2026');

    // Unclassified page: no classification strip at all.
    expect(el(fixture).querySelector('[data-testid="project-wiki-class-concepts/page.html"]')).toBeNull();
    http.verify();
  });

  it('shows the classification block in the meta panel and opens the successor via the supersededBy link', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!
      .click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    const panel = el(fixture).querySelector('[data-testid="project-wiki-classification-panel"]');
    expect(panel, 'classification block').toBeTruthy();
    expect(panel!.textContent).toContain('Klassifikation');

    // Status chip in tree optics; the successor renders as a navigable link.
    const chip = panel!.querySelector('[data-testid="project-wiki-classification-status-chip"]');
    expect(chip?.textContent?.trim()).toBe('überholt');
    expect(chip?.getAttribute('data-tone')).toBe('superseded');

    // Type is spelled out, not the compact tree code; analysis date is visible.
    const type = panel!.querySelector('[data-testid="project-wiki-classification-type"]');
    expect(type?.textContent).toContain('Konzept');
    expect(type?.textContent).not.toContain('KON');
    const analyzed = panel!.querySelector('[data-testid="project-wiki-classification-analyzed"]');
    expect(analyzed?.textContent).toContain('Analyse');
    expect(analyzed?.textContent).toContain('18.07.2026');

    // Clicking the supersededBy link opens the successor page via openFile.
    const link = panel!.querySelector<HTMLButtonElement>('[data-testid="project-wiki-classification-superseded-link"]');
    expect(link?.textContent?.trim()).toBe('concepts/new-overview.md');
    link!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/new-overview.md')
      .flush({ relPath: 'concepts/new-overview.md', content: '# New overview' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/new-overview.md')
      .flush({ ...HISTORY, relPath: 'concepts/new-overview.md' });
    fixture.detectChanges();

    expect(fixture.componentInstance.openedRel()).toBe('concepts/new-overview.md');
    // The successor is not in the tree / unclassified, so the block hides again.
    expect(el(fixture).querySelector('[data-testid="project-wiki-classification-panel"]')).toBeNull();
    http.verify();
  });

  it('hides the classification block for a page without classification', async () => {
    const { fixture, http } = await setup();
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-README.md"]')!
      .click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/README.md')
      .flush({ relPath: 'README.md', content: '# Index' });
    http.expectOne('/api/projects/Demo/wiki/history/README.md')
      .flush({ ...HISTORY, relPath: 'README.md' });
    fixture.detectChanges();

    expect(el(fixture).querySelector('[data-testid="project-wiki-meta-panel"]'), 'meta panel').toBeTruthy();
    expect(el(fixture).querySelector('[data-testid="project-wiki-classification-panel"]')).toBeNull();
    http.verify();
  });

  it('shows applicable repository style guides and opens one in the Wiki reader', async () => {
    const { fixture, http } = await setup();

    const panel = el(fixture).querySelector('[data-testid="project-wiki-style-guides"]');
    expect(panel?.textContent).toContain('Engineering style guides');
    expect(panel?.textContent).toContain('Angular');
    expect(panel?.textContent).toContain('.NET backend guide');
    expect(panel?.textContent).toContain('Prompt context · v1');

    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-style-guide-angular-components"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/quality/angular-components.md')
      .flush({ relPath: 'quality/angular-components.md', content: '# Angular component guide' });
    http.expectOne('/api/projects/Demo/wiki/history/quality/angular-components.md').flush(HISTORY);
    fixture.detectChanges();

    expect(fixture.componentInstance.openedRel()).toBe('quality/angular-components.md');
    expect(el(fixture).textContent).toContain('Angular component guide');
    http.verify();
  });

  it('loads a document and its history on click', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    const btn = el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/overview.md"]');
    expect(btn).toBeTruthy();
    btn!.click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    expect(el(fixture).textContent).toContain('Hello wiki');

    // The Source tab exposes a read-only editor surface with line numbers.
    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-tab-source"]')!.click();
    fixture.detectChanges();
    const source = el(fixture).querySelector('[data-testid="project-wiki-source-editor"]');
    expect(source, 'source editor').toBeTruthy();
    expect(source!.textContent).toContain('# Hello wiki');
    expect(source!.querySelector('[data-testid="project-wiki-source-line"]')!.textContent).toContain('1');

    // The Report tab loads the linked metadata reasoning HTML as a third view.
    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-tab-report"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md.report.html')
      .flush({
        relPath: 'concepts/overview.md.report.html',
        content: '<main><h1>Concept overview report</h1><p>Why drift: sampled evidence.</p></main>',
      });
    fixture.detectChanges();
    const reportFrame = el(fixture).querySelector<HTMLIFrameElement>('[data-testid="project-wiki-report-frame"]');
    expect(reportFrame, 'report iframe').toBeTruthy();
    expect(reportFrame!.getAttribute('sandbox')).toBe('allow-scripts');
    expect(reportFrame!.getAttribute('sandbox')).not.toContain('allow-same-origin');
    expect(reportFrame!.getAttribute('srcdoc') ?? reportFrame!.srcdoc).toContain('Why drift');

    // History is no longer a document tab; it lives in the right context rail.
    const text = el(fixture).textContent ?? '';
    expect(el(fixture).querySelector('[data-testid="project-wiki-history-panel"]')).toBeTruthy();
    expect(text).toContain('Claude Opus 4.8');
    expect(text).toContain('abc1234');
    http.verify();
  });

  it('opens the report tab at the matching heading when a classification chip is clicked', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    const metric = el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-metric-concepts/overview.md-drift"]');
    expect(metric).toBeTruthy();
    metric!.click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md.report.html')
      .flush({
        relPath: 'concepts/overview.md.report.html',
        content: '<!doctype html><html><head><title>r</title></head><body><h2 id="why-drift">Why drift?</h2></body></html>',
      });
    fixture.detectChanges();

    expect(el(fixture).querySelector('[data-testid="project-wiki-tab-report"]')?.className)
      .toContain('pwiki__tab--active');
    const srcdoc = el(fixture)
      .querySelector<HTMLIFrameElement>('[data-testid="project-wiki-report-frame"]')!
      .getAttribute('srcdoc') ?? '';
    expect(srcdoc).toContain('url=#why-drift');
    http.verify();
  });

  it('opens Markdown edit mode and saves through the wiki file API', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-edit"]')!.click();
    fixture.detectChanges();
    expect(el(fixture).querySelector('[data-testid="project-wiki-editor-shell"]')).toBeTruthy();

    fixture.componentInstance.saveWikiContent('# Changed\n');
    const save = http.expectOne(req =>
      req.method === 'PUT' && req.url === '/api/projects/Demo/wiki/files/concepts/overview.md');
    expect(save.request.body).toEqual({ content: '# Changed\n' });
    save.flush({
      relPath: 'concepts/overview.md',
      saved: true,
      changed: true,
      sha: 'def123456',
      branch: 'docs-branch',
    });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    http.expectOne('/api/projects/Demo/wiki/tree').flush(TREE);
    flushWikiPulse(http);
    fixture.detectChanges();

    expect(fixture.componentInstance.openedContent()).toBe('# Changed\n');
    expect(el(fixture).querySelector('[data-testid="project-wiki-save-result"]')!.textContent)
      .toContain('docs-branch');
    http.verify();
  });

  it('shows last-modified (date, author, commit subject) in the doc header from git metadata', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    // Doc tab is the default: the header surfaces the newest commit's author + subject.
    const meta = el(fixture).querySelector('[data-testid="project-wiki-last-modified"]');
    expect(meta, 'last-modified header').toBeTruthy();
    expect(meta!.querySelector('[data-testid="project-wiki-last-modified-author"]')!.textContent).toContain('bot');
    expect(meta!.querySelector('[data-testid="project-wiki-last-modified-subject"]')!.textContent).toContain('update');
    http.verify();
  });

  it('filters the tree by needle', async () => {
    const { fixture, http } = await setup();
    fixture.componentInstance.filter.set('concept');
    fixture.detectChanges();
    const text = el(fixture).querySelector('[data-testid="project-wiki-tree"]')?.textContent ?? '';
    expect(text).toContain('Concept overview');
    expect(text).not.toContain('Docs index');
    http.verify();
  });

  it('opens on the generated Pulse landing view and collapses side panels', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    // The wiki opens on Pulse (not a page), with its two quick actions + aside.
    expect(root.querySelector('[data-testid="project-wiki-viewer-empty"]')!.textContent)
      .toContain('Pulse');
    expect(root.querySelector('[data-testid="project-wiki-pulse"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-open-first"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-workspace-meta"]')).toBeTruthy();

    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-toggle-nav"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-tree"]')).toBeNull();

    // The meta rail folds via its own labelled head toggle (not a top-bar
    // mini-icon): the rail stays mounted as a slim strip, the body hides.
    const metaToggle = root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-meta-toggle"]')!;
    expect(metaToggle, 'meta toggle head').toBeTruthy();
    expect(metaToggle.getAttribute('aria-expanded')).toBe('true');
    metaToggle.click();
    fixture.detectChanges();
    expect(metaToggle.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('#project-wiki-meta-body')!.hasAttribute('hidden')).toBe(true);
    http.verify();
  });

  it('renders the dashboard head stats from the Pulse payload', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    // Page total comes from the tree, drift numbers from pulse.drift.counts.
    expect(root.querySelector('[data-testid="project-wiki-stat-pages"]')?.textContent).toContain('4');
    const drift = root.querySelector('[data-testid="project-wiki-stat-drift"]')!;
    expect(drift.textContent).toContain('Fresh');
    expect(drift.textContent).toContain('Aging');
    expect(drift.textContent).toContain('Stale');
    // Overall verdict reads as a small icon + label, not a coloured number.
    expect(root.querySelector('[data-testid="project-wiki-pulse-overall"]')?.textContent)
      .toContain('Drift Aging');
    http.verify();
  });

  it('hides the Aufmerksamkeit card when warnings and inbox are clear', async () => {
    const clearPulse: WikiPulse = {
      ...PULSE,
      inbox: { available: true, reason: null, count: 0, items: [] },
      warnings: { available: true, reason: 'All clear.', count: 0, items: [] },
    };
    const { fixture, http } = await setup(TREE, clearPulse);
    const root = el(fixture);

    expect(root.querySelector('[data-testid="project-wiki-pulse-attention"]')).toBeNull();
    expect(root.querySelector('[data-testid="project-wiki-pulse-inbox"]')).toBeNull();
    // The feed and drift cards stay regardless of the attention state.
    expect(root.querySelector('[data-testid="project-wiki-pulse-feed"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-pulse-drift"]')).toBeTruthy();
    http.verify();
  });

  it('renders the Pulse feed, inbox, and drift bar and opens a feed page', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    // Change feed row carries its task key + area/drift segment renders.
    expect(root.querySelector('[data-testid="project-wiki-pulse-task-concepts/overview.md"]')?.textContent)
      .toContain('AGT-2014');
    expect(root.querySelector('[data-testid="project-wiki-pulse-area-10-current-development-state"]')?.textContent)
      .toContain('Aging');
    // Inbox lists the loose page; overall drift chip shows the worst grade.
    expect(root.querySelector('[data-testid="project-wiki-pulse-inbox-open-stray.md"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-pulse-overall"]')?.textContent).toContain('Aging');

    // Clicking a feed row opens the page in the reader.
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-pulse-feed-open-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')?.textContent)
      .toContain('concepts/overview.md');
    http.verify();
  });

  it('restores collapsed panels, selected document, and active tab from localStorage', async () => {
    localStorage.setItem(wikiStorageKey(), JSON.stringify({
      navCollapsed: true,
      contextCollapsed: true,
      openedRel: 'concepts/overview.md',
      viewerTab: 'source',
      navWidth: 340,
      contextWidth: 360,
      expandedIds: ['concepts'],
    }));

    const { fixture, http } = await setup();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Restored\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    const root = el(fixture);
    expect(root.querySelector('[data-testid="project-wiki-tree"]')).toBeNull();
    // The collapsed rail stays mounted (grade badge remains visible); only the
    // meta body folds, and the head reports the collapsed state via aria.
    expect(root.querySelector('[data-testid="project-wiki-meta-panel"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-meta-toggle"]')!.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('#project-wiki-meta-body')!.hasAttribute('hidden')).toBe(true);
    expect(root.querySelector('[data-testid="project-wiki-source-editor"]')!.textContent).toContain('# Restored');
    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent).toContain('concepts/overview.md');
    expect(fixture.componentInstance.navWidth()).toBe(340);
    expect(fixture.componentInstance.contextWidth()).toBe(360);
    expect(JSON.parse(localStorage.getItem(wikiStorageKey()) ?? '{}').expandedIds).toContain('concepts');
    http.verify();
  });

  it('surfaces the page drift grade in the meta head, visible even when the rail is collapsed', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    // The grade lifted from the drift-metadata card into the head reads the
    // companion driftGrade (B, "warn" tone) so it is legible at a glance.
    const grade = el(fixture).querySelector('[data-testid="project-wiki-meta-grade"]')!;
    expect(grade.textContent).toContain('B');
    expect(grade.getAttribute('data-tone')).toBe('warn');

    // Collapsing the rail keeps the grade badge mounted in the (now vertical) head.
    fixture.componentInstance.toggleContext();
    fixture.detectChanges();
    const toggle = el(fixture).querySelector('[data-testid="project-wiki-meta-toggle"]')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(el(fixture).querySelector('[data-testid="project-wiki-meta-grade"]')!.textContent).toContain('B');
    expect(el(fixture).querySelector('#project-wiki-meta-body')!.hasAttribute('hidden')).toBe(true);
    http.verify();
  });

  it('remembers the meta-rail collapse state per page', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    const cmp = fixture.componentInstance;

    const openReadmeHistory = () => http.expectOne('/api/projects/Demo/wiki/history/README.md').flush({
      relPath: 'README.md', model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: null, status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    });

    // Open overview.md and fold its meta rail.
    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Overview\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();
    expect(cmp.contextCollapsed()).toBe(false);
    cmp.toggleContext();
    expect(cmp.contextCollapsed()).toBe(true);

    // A different page keeps its own state (default: expanded).
    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-README.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/README.md')
      .flush({ relPath: 'README.md', content: '# Readme\n' });
    openReadmeHistory();
    fixture.detectChanges();
    expect(cmp.contextCollapsed()).toBe(false);

    // Reopening overview.md restores its remembered collapsed state.
    el(fixture).querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Overview\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();
    expect(cmp.contextCollapsed()).toBe(true);

    // The per-page choice is persisted for a later session.
    const stored = JSON.parse(localStorage.getItem(wikiStorageKey()) ?? '{}');
    expect(stored.metaCollapsedByPage?.['concepts/overview.md']).toBe(true);
    // README was only viewed, never toggled, so it keeps the default (no entry).
    expect(stored.metaCollapsedByPage?.['README.md']).toBeUndefined();
    http.verify();
  });

  it('restores the report tab and loads the linked reasoning report from localStorage', async () => {
    localStorage.setItem(wikiStorageKey(), JSON.stringify({
      openedRel: 'concepts/overview.md',
      viewerTab: 'report',
      expandedIds: ['concepts'],
    }));

    const { fixture, http } = await setup();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Restored report\n\nBody text.' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md.report.html')
      .flush({
        relPath: 'concepts/overview.md.report.html',
        content: '<main><h1>Restored reasoning report</h1></main>',
      });
    fixture.detectChanges();

    const root = el(fixture);
    expect(root.querySelector('[data-testid="project-wiki-tab-report"]')?.className)
      .toContain('pwiki__tab--active');
    expect(root.querySelector('[data-testid="project-wiki-report-frame"]')!).toBeTruthy();
    http.verify();
  });

  it('resizes and persists the tree and context panel widths', async () => {
    const { fixture, http } = await setup();
    const cmp = fixture.componentInstance;

    cmp.startPanelResize(fakePointer(100), 'nav');
    cmp.resizePanel(fakePointer(150));
    cmp.finishPanelResize(fakePointer(150));
    expect(cmp.navWidth()).toBe(336);

    cmp.startPanelResize(fakePointer(300), 'context');
    cmp.resizePanel(fakePointer(250));
    cmp.finishPanelResize(fakePointer(250));
    expect(cmp.contextWidth()).toBe(334);

    const stored = JSON.parse(localStorage.getItem(wikiStorageKey()) ?? '{}') as {
      navWidth?: number;
      contextWidth?: number;
    };
    expect(stored.navWidth).toBe(336);
    expect(stored.contextWidth).toBe(334);

    cmp.onPanelSplitterKeydown(new KeyboardEvent('keydown', { key: 'ArrowRight' }), 'nav');
    expect(cmp.navWidth()).toBe(352);
    cmp.onPanelSplitterKeydown(new KeyboardEvent('keydown', { key: 'ArrowLeft' }), 'context');
    expect(cmp.contextWidth()).toBe(350);
    http.verify();
  });

  it('opens the drift modal with model selection and a document-scoped prompt', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-drift-open-empty"]')!.click();
    fixture.detectChanges();

    http.expectOne('/api/cli/claude/models').flush({
      models: [
        { id: 'claude-opus-4-8', label: 'Claude Opus 4.8', multiplier: null, vendor: 'anthropic', isDefault: true },
      ],
      source: 'test',
    });
    http.expectOne('/api/watch-paths').flush([
      { name: 'Demo', path: 'C:\\repo\\projects\\agent-taskboard' },
    ]);
    http.expectOne(req =>
      req.method === 'GET' && req.urlWithParams.startsWith('/api/drift/agent-taskboard/reports?')
    ).flush({ reports: [] });
    http.expectOne('/api/drift/agent-taskboard/actions/software-architecture-drift/prompt')
      .flush({
        project: 'agent-taskboard',
        capturedAt: '2026-06-11T00:00:00Z',
        architectureModelFound: true,
        architectureModelSourcePath: 'docs/architecture/model.md',
        architectureModelRejectionReason: null,
        docs: ['docs/architecture/model.md'],
        sourceTree: [],
        moduleBoundaries: [],
        schemas: [],
        testDirs: [],
        recentTasks: [],
        recentDriftReports: [],
        recentAnalysisReports: [],
        prompt: 'Base architecture drift prompt',
      });
    fixture.detectChanges();

    const modal = document.querySelector<HTMLElement>('[data-testid="project-wiki-drift-modal"]');
    expect(modal, 'drift modal').toBeTruthy();
    expect(modal!.querySelector('[data-testid="project-wiki-drift-model"]')!.textContent)
      .toContain('Claude Opus 4.8');
    expect(modal!.querySelector('[data-testid="project-wiki-drift-result"]')!.textContent)
      .toContain('Knowledge page drift analysis');
    expect(modal!.querySelector('[data-testid="project-wiki-drift-result"]')!.textContent)
      .toContain('Base architecture drift prompt');
    http.verify();
  });

  it('opens a TEXT-ONLY right-click context menu for files and folders', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    expandConcepts(fixture);

    const openCtx = (id: string) => {
      const row = root.querySelector<HTMLElement>(`[data-testid="project-wiki-node-${id}"]`);
      expect(row, `row ${id}`).toBeTruthy();
      row!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 40, clientY: 40 }));
      fixture.detectChanges();
    };

    const assertTextOnly = (panel: HTMLElement) => {
      expect(panel.querySelectorAll('img')).toHaveLength(0);
      expect(panel.querySelectorAll('svg')).toHaveLength(0);
      expect(panel.querySelectorAll('.app-menu__icon')).toHaveLength(0);
    };

    // File context menu: Rename + View history + Delete, text-only.
    openCtx('concepts/overview.md');
    let panel = document.querySelector<HTMLElement>('[data-testid="wiki-ctx-panel"]');
    expect(panel, 'file context menu panel').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-rename"]')).toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-history"]')).toBeTruthy();
    assertTextOnly(panel!);

    fixture.componentInstance.closeMenu();
    fixture.detectChanges();

    // Category context menu: New page + New category + Rename + Delete category, text-only.
    openCtx('concepts');
    panel = document.querySelector<HTMLElement>('[data-testid="wiki-ctx-panel"]');
    expect(panel, 'folder context menu panel').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-new-page"]')).toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-new-folder"]')).toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-delete"]')!.textContent).toContain('Delete');
    assertTextOnly(panel!);
    http.verify();
  });

  it('renders an HTML doc inside a script-enabled opaque-origin sandboxed iframe', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/page.html"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/page.html')
      .flush({ relPath: 'concepts/page.html', content: '<h1>Sandboxed</h1><script>window.x=1</script>' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/page.html').flush({
      relPath: 'concepts/page.html', model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: null, status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    });
    fixture.detectChanges();

    const frame = el(fixture).querySelector<HTMLIFrameElement>('[data-testid="project-wiki-html-frame"]');
    expect(frame, 'html iframe').toBeTruthy();
    expect(frame!.getAttribute('sandbox')).toBe('allow-scripts');
    expect(frame!.getAttribute('sandbox')).not.toContain('allow-same-origin');
    const srcdoc = frame!.getAttribute('srcdoc') ?? frame!.srcdoc;
    expect(srcdoc).toContain('Sandboxed');
    http.verify();
  });

  it('renders JSON metadata in preview and source modes', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/page.metadata.json"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/page.metadata.json')
      .flush({ relPath: 'concepts/page.metadata.json', content: '{"title":"Page metadata","drift":{"grade":"B"}}' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/page.metadata.json').flush({
      relPath: 'concepts/page.metadata.json', model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: null, status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    });
    fixture.detectChanges();

    expect(el(fixture).querySelector('[data-testid="project-wiki-json-preview"]')!.textContent)
      .toContain('"grade": "B"');

    el(fixture).querySelector<HTMLButtonElement>('[data-testid="project-wiki-tab-source"]')!.click();
    fixture.detectChanges();
    expect(el(fixture).querySelector('[data-testid="project-wiki-source-editor"]')!.textContent)
      .toContain('"title":"Page metadata"');
    http.verify();
  });

  it('creates a page via a git-backed POST then re-reads the tree', async () => {
    const { fixture, http } = await setup();
    fixture.componentInstance.createPage('', 'guide.md');
    fixture.detectChanges();

    const post = http.expectOne(req =>
      req.method === 'POST' && req.url === '/api/projects/Demo/wiki/pages');
    expect(post.request.body).toEqual({ relPath: 'guide.md', content: null });
    post.flush({ relPath: 'docs/guide.md', sha: 'deadbee' });

    // The mutation triggers a refresh of the physical tree.
    http.expectOne('/api/projects/Demo/wiki/tree').flush(TREE);
    flushWikiPulse(http);
    fixture.detectChanges();
    http.verify();
  });

  it('drag-drops a file onto a folder and moves it via git', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    const fileRow = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-README.md"]');
    const folderRow = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-concepts"]');
    expect(fileRow).toBeTruthy();
    expect(folderRow).toBeTruthy();

    const dt = makeDataTransfer();
    fireDrag(fileRow!, 'dragstart', dt);
    fireDrag(folderRow!, 'dragover', dt);
    fireDrag(folderRow!, 'drop', dt);
    fixture.detectChanges();

    const post = http.expectOne(req =>
      req.method === 'POST' && req.url === '/api/projects/Demo/wiki/move');
    expect(post.request.body).toEqual({ fromRelPath: 'README.md', toRelPath: 'concepts/README.md' });
    post.flush({ from: 'README.md', to: 'concepts/README.md', sha: 'abc1234' });

    http.expectOne('/api/projects/Demo/wiki/tree').flush(TREE);
    flushWikiPulse(http);
    fixture.detectChanges();
    http.verify();
  });

  it('drag-drops a folder onto a sibling folder and persists the category order', async () => {
    const { fixture, http } = await setup(REORDER_TREE);
    const root = el(fixture);

    const alpha = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-alpha"]')!;
    const gamma = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-gamma"]')!;
    expect(alpha).toBeTruthy();
    expect(gamma).toBeTruthy();
    // Folder rows are draggable (only frame nodes are not).
    expect(alpha.getAttribute('draggable')).toBe('true');

    // A folder never drops onto a folder with a different parent: no request.
    fixture.componentInstance.toggleExpand('beta');
    fixture.detectChanges();
    const inner = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-beta/inner"]')!;
    expect(inner).toBeTruthy();
    const crossDt = makeDataTransfer();
    fireDrag(alpha, 'dragstart', crossDt);
    fireDrag(inner, 'dragover', crossDt);
    fireDrag(inner, 'drop', crossDt);
    fixture.detectChanges();

    // Dragging alpha down onto sibling gamma: alpha takes the slot after gamma.
    const dt = makeDataTransfer();
    fireDrag(alpha, 'dragstart', dt);
    fireDrag(gamma, 'dragover', dt);
    fireDrag(gamma, 'drop', dt);
    fixture.detectChanges();

    const put = http.expectOne(req =>
      req.method === 'PUT' && req.url === '/api/projects/Demo/wiki/folder-order');
    expect(put.request.body).toEqual({ parentRelPath: '', orderedNames: ['beta', 'gamma', 'alpha'] });
    put.flush({ relPath: '.wiki-order.json', sha: 'abc1234' });

    // The mutation triggers a refresh; the tree comes back in the saved order.
    http.expectOne('/api/projects/Demo/wiki/tree').flush({
      ...REORDER_TREE,
      root: [REORDER_TREE.root[1], REORDER_TREE.root[2], REORDER_TREE.root[0]],
    });
    flushWikiPulse(http);
    fixture.detectChanges();

    const labels = [...root.querySelectorAll('[data-testid^="project-wiki-folder-label-"]')]
      .map(n => (n.getAttribute('data-testid') ?? '').replace('project-wiki-folder-label-', ''))
      .filter(id => !id.includes('/'));
    expect(labels).toEqual(['beta', 'gamma', 'alpha']);
    http.verify();
  });

  it('pins an Overview node above the categories that reopens the dashboard landing', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    const node = root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-overview-node"]')!;
    expect(node).toBeTruthy();
    // It sits directly above the sortable category rows and is not draggable.
    expect(node.nextElementSibling?.classList.contains('pwiki__rows')).toBe(true);
    expect(node.getAttribute('draggable')).toBeNull();
    // The landing is the initial view, so the pinned node starts active.
    expect(node.classList.contains('pwiki__overview-node--active')).toBe(true);
    expect(root.querySelector('[data-testid="project-wiki-viewer-empty"]')).toBeTruthy();

    // Opening a document deactivates it.
    expandConcepts(fixture);
    root.querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-overview-node"]')!
      .classList.contains('pwiki__overview-node--active')).toBe(false);
    expect(root.querySelector('[data-testid="project-wiki-viewer-empty"]')).toBeFalsy();

    // Clicking Overview closes the page: the same state as the initial landing.
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-overview-node"]')!.click();
    fixture.detectChanges();
    http.match('/api/projects/Demo/style-guides').forEach(r => r.flush(STYLE_GUIDES));
    http.match('/api/projects/Demo/wiki/home').forEach(r => r.flush(HOME));
    fixture.detectChanges();

    expect(fixture.componentInstance.openedRel()).toBeNull();
    expect(fixture.componentInstance.selectedFolderRel()).toBeNull();
    expect(root.querySelector('[data-testid="project-wiki-viewer-empty"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-overview-node"]')!
      .classList.contains('pwiki__overview-node--active')).toBe(true);

    // From a folder overview the node also routes back to the landing.
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-folder-label-concepts"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts')
      .flush({ path: 'concepts', name: 'concepts', children: [] });
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-overview-node"]')!
      .classList.contains('pwiki__overview-node--active')).toBe(false);

    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-overview-node"]')!.click();
    fixture.detectChanges();
    http.match('/api/projects/Demo/style-guides').forEach(r => r.flush(STYLE_GUIDES));
    http.match('/api/projects/Demo/wiki/home').forEach(r => r.flush(HOME));
    fixture.detectChanges();
    expect(fixture.componentInstance.selectedFolderRel()).toBeNull();
    expect(root.querySelector('[data-testid="project-wiki-viewer-empty"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-overview-node"]')!
      .classList.contains('pwiki__overview-node--active')).toBe(true);
    http.verify();
  });

  it('previews an old revision from the history panel', async () => {
    const { fixture, http } = await setup();
    expandConcepts(fixture);
    el(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!
      .click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Current\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    // History is in the right context rail; clicking View fetches the old revision.
    el(fixture).querySelector<HTMLButtonElement>('[data-testid="wiki-doc-history-view-abc1234"]')!.click();
    fixture.detectChanges();

    http.expectOne('/api/projects/Demo/wiki/revisions/abc/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', sha: 'abc', content: '# Old revision\n' });
    fixture.detectChanges();

    const text = el(fixture).textContent ?? '';
    expect(text).toContain('Old revision');
    expect(el(fixture).querySelector('[data-testid="project-wiki-rev-banner"]')).toBeTruthy();
    http.verify();
  });

  it('supports arrow-key tree navigation and persists expanded folders', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    const tree = root.querySelector<HTMLElement>('[data-testid="project-wiki-tree"]')!;
    expect(tree).toBeTruthy();
    const folder = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-concepts"]')!;
    folder.querySelector<HTMLButtonElement>('.pwiki__label')!.click();
    fixture.detectChanges();
    // Clicking the folder *name* now also selects the folder (overview page).
    http.expectOne('/api/projects/Demo/wiki/folder/concepts')
      .flush({ path: 'concepts', name: 'concepts', children: [] });
    fixture.detectChanges();
    await Promise.resolve();

    expect(document.activeElement?.getAttribute('data-testid')).toBe('project-wiki-node-concepts');
    expect(folder.getAttribute('aria-expanded')).toBe('true');

    folder.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    fixture.detectChanges();
    expect(folder.getAttribute('aria-expanded')).toBe('false');

    folder.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="project-wiki-file-concepts/overview.md"]')).toBeTruthy();
    expect(JSON.parse(localStorage.getItem(wikiStorageKey()) ?? '{}').expandedIds).toContain('concepts');

    folder.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    fixture.detectChanges();
    await Promise.resolve();
    expect(document.activeElement?.getAttribute('data-testid')).toBe('project-wiki-node-concepts/overview.md');

    document.activeElement?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Keyboard\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('concepts/overview.md');
    http.verify();
  });

  // ---- folder overview page (content pane) ----

  const FOLDER_CONCEPTS: WikiFolderOverview = {
    path: 'concepts',
    name: 'Concepts',
    children: [
      // Pages deliberately listed before the folder: the view sorts folders first.
      {
        name: 'overview.md', relPath: 'concepts/overview.md', kind: 'page', fileType: 'md',
        title: 'Concept overview', summary: 'Der rote Faden.',
        updatedAt: '2026-07-10T08:00:00Z', size: 2048, childCount: null,
      },
      {
        name: 'page.html', relPath: 'concepts/page.html', kind: 'page', fileType: 'html',
        title: 'HTML page', summary: null,
        updatedAt: '2026-07-01T08:00:00Z', size: 500, childCount: null,
      },
      {
        name: 'deep', relPath: 'concepts/deep', kind: 'folder', fileType: null,
        title: 'Deep dive', summary: 'Unterordner mit Details.',
        updatedAt: '2026-07-11T08:00:00Z', size: null, childCount: 3,
      },
    ],
  };

  it('shows a folder overview when the folder name is clicked and routes row clicks', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    // Clicking the folder NAME selects it (the chevron would only expand).
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-folder-label-concepts"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts').flush(FOLDER_CONCEPTS);
    fixture.detectChanges();

    const view = root.querySelector('[data-testid="wiki-folder-view"]')!;
    expect(view, 'folder overview').toBeTruthy();
    // Column headers: Titel | Datei | Typ | Status | Geändert | Größe.
    const headers = [...view.querySelectorAll('th')].map(th => th.textContent?.trim());
    expect(headers).toEqual(['Titel', 'Datei', 'Typ', 'Status', 'Geändert', 'Größe']);
    // Folders first, then pages in payload order.
    const rowIds = [...view.querySelectorAll('[data-testid^="wiki-folder-row-"]')]
      .map(row => row.getAttribute('data-testid'));
    expect(rowIds).toEqual([
      'wiki-folder-row-concepts/deep',
      'wiki-folder-row-concepts/overview.md',
      'wiki-folder-row-concepts/page.html',
    ]);
    // Type / size formatting incl. child count for folders, summary second line.
    expect(view.querySelector('[data-testid="wiki-folder-type-concepts/deep"]')!.textContent).toContain('Ordner');
    expect(view.querySelector('[data-testid="wiki-folder-size-concepts/deep"]')!.textContent).toContain('3 Einträge');
    expect(view.querySelector('[data-testid="wiki-folder-type-concepts/page.html"]')!.textContent).toContain('html');
    expect(view.querySelector('[data-testid="wiki-folder-size-concepts/overview.md"]')!.textContent).toContain('2.0 KB');
    expect(view.querySelector('[data-testid="wiki-folder-size-concepts/page.html"]')!.textContent).toContain('500 B');
    expect(view.querySelector('[data-testid="wiki-folder-summary-concepts/overview.md"]')!.textContent)
      .toContain('Der rote Faden.');

    // Folder row click drills into the subfolder overview.
    root.querySelector<HTMLElement>('[data-testid="wiki-folder-row-concepts/deep"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts/deep').flush({
      path: 'concepts/deep',
      name: 'Deep dive',
      children: [{
        name: 'detail.md', relPath: 'concepts/deep/detail.md', kind: 'page', fileType: 'md',
        title: 'Detail', summary: null, updatedAt: null, size: 10, childCount: null,
      }],
    });
    fixture.detectChanges();
    expect(el(fixture).querySelector('[data-testid="wiki-folder-title"]')!.textContent).toContain('Deep dive');
    // The breadcrumb carries the clickable parent segment.
    expect(el(fixture).querySelector('[data-testid="wiki-folder-crumb-concepts"]')).toBeTruthy();

    // Page row click opens the page in the reader.
    el(fixture).querySelector<HTMLElement>('[data-testid="wiki-folder-row-concepts/deep/detail.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/deep/detail.md')
      .flush({ relPath: 'concepts/deep/detail.md', content: '# Detail\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/deep/detail.md').flush({
      relPath: 'concepts/deep/detail.md', model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: null, status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    });
    fixture.detectChanges();
    expect(el(fixture).querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('concepts/deep/detail.md');
    http.verify();
  });

  it('shows loading and error states for the folder overview', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-folder-label-concepts"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="wiki-folder-loading"]')).toBeTruthy();
    http.expectOne('/api/projects/Demo/wiki/folder/concepts')
      .flush({ error: 'boom' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="wiki-folder-error"]')).toBeTruthy();
    http.verify();
  });

  // ---- wiki search ----

  const searchRequest = (http: HttpTestingController, q: string, semantic = false) =>
    http.expectOne(r => r.url === '/api/projects/Demo/wiki/search'
      && r.params.get('q') === q
      && (semantic ? r.params.get('semantic') === 'true' : !r.params.has('semantic')));

  const searchResponse = (overrides: Partial<WikiSearchResponse> = {}): WikiSearchResponse => ({
    query: 'guide',
    semanticUsed: false,
    expandedTerms: [],
    durationMs: 12,
    results: [{
      relPath: 'concepts/overview.md',
      title: 'Concept overview',
      kind: 'md',
      snippet: 'Der <em>Guide</em> für alles',
      score: 0.9,
      updatedAt: '2026-07-10T08:00:00Z',
    }],
    ...overrides,
  });

  it('debounces the search (300ms, min 2 chars) and renders em-snippets safely', async () => {
    const { fixture, http } = await setup();
    const cmp = fixture.componentInstance;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      // A single character never searches.
      cmp.onSearchQueryChange('g');
      vi.advanceTimersByTime(400);
      http.expectNone(r => r.url === '/api/projects/Demo/wiki/search');

      // Retyping inside the window resets the debounce.
      cmp.onSearchQueryChange('gu');
      vi.advanceTimersByTime(200);
      cmp.onSearchQueryChange('guide');
      vi.advanceTimersByTime(299);
      http.expectNone(r => r.url === '/api/projects/Demo/wiki/search');
      vi.advanceTimersByTime(1);

      // The snippet keeps only <em>; injected markup arrives escaped as text.
      searchRequest(http, 'guide').flush(searchResponse({
        results: [{
          relPath: 'concepts/overview.md',
          title: 'Concept overview',
          kind: 'md',
          snippet: 'Der <em>Guide</em> mit <img src=x onerror=alert(1)> und <em onclick=alert(1)>Trick</em>',
          score: 0.9,
          updatedAt: '2026-07-10T08:00:00Z',
        }],
      }));
      fixture.detectChanges();

      const snippet = el(fixture)
        .querySelector('[data-testid="wiki-search-snippet-concepts/overview.md"]')!;
      expect(snippet, 'snippet').toBeTruthy();
      expect(snippet.querySelectorAll('em')).toHaveLength(1);
      expect(snippet.querySelector('em')!.textContent).toBe('Guide');
      expect(snippet.querySelector('img')).toBeNull();
      expect(snippet.textContent).toContain('<img src=x onerror=alert(1)>');
      expect(snippet.textContent).toContain('<em onclick=alert(1)>Trick');
      // Result meta: dimmed relPath + relative time render alongside the title.
      const row = el(fixture).querySelector('[data-testid="wiki-search-open-concepts/overview.md"]')!;
      expect(row.textContent).toContain('Concept overview');
      expect(row.querySelector('code')!.textContent).toContain('concepts/overview.md');
      expect(row.querySelector('time')).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
    http.verify();
  });

  it('opens the top search hit on Enter', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      const input = root.querySelector<HTMLInputElement>('[data-testid="project-wiki-search"]')!;
      input.value = 'guide';
      input.dispatchEvent(new Event('input', { bubbles: true }));
      fixture.detectChanges();
      vi.advanceTimersByTime(300);
      searchRequest(http, 'guide').flush(searchResponse());
      fixture.detectChanges();

      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      fixture.detectChanges();
      http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
        .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n' });
      http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
      fixture.detectChanges();

      expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
        .toContain('concepts/overview.md');
      // Opening a hit leaves the search: the box is cleared again.
      expect(fixture.componentInstance.searchQuery()).toBe('');
    } finally {
      vi.useRealTimers();
    }
    http.verify();
  });

  it('Escape cancels a pending search and restores the previous view', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    expandConcepts(fixture);
    root.querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      const input = root.querySelector<HTMLInputElement>('[data-testid="project-wiki-search"]')!;
      input.value = 'over';
      input.dispatchEvent(new Event('input', { bubbles: true }));
      fixture.detectChanges();
      // Search view replaces the reader while the query is active...
      expect(root.querySelector('[data-testid="wiki-search-results"]')).toBeTruthy();
      expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')).toBeNull();

      // ...Escape before the debounce fires: no request, previous view returns.
      vi.advanceTimersByTime(100);
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      fixture.detectChanges();
      vi.advanceTimersByTime(400);
      http.expectNone(r => r.url === '/api/projects/Demo/wiki/search');
      expect(root.querySelector('[data-testid="wiki-search-results"]')).toBeNull();
      expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
        .toContain('concepts/overview.md');
    } finally {
      vi.useRealTimers();
    }
    http.verify();
  });

  it('expands the search semantically and shows the expanded terms as chips', async () => {
    const { fixture, http } = await setup();
    const cmp = fixture.componentInstance;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      cmp.onSearchQueryChange('deploy');
      vi.advanceTimersByTime(300);
      searchRequest(http, 'deploy').flush(searchResponse({ query: 'deploy' }));
      fixture.detectChanges();
      // The lexical response never shows the unavailable hint.
      expect(el(fixture).querySelector('[data-testid="wiki-search-semantic-unavailable"]')).toBeNull();

      el(fixture).querySelector<HTMLButtonElement>('[data-testid="wiki-search-semantic"]')!.click();
      fixture.detectChanges();
      // Spinner while the semantic call runs; the current results stay visible.
      expect(el(fixture).querySelector('[data-testid="wiki-search-semantic-spinner"]')).toBeTruthy();
      expect(el(fixture).querySelector('[data-testid="wiki-search-open-concepts/overview.md"]')).toBeTruthy();

      searchRequest(http, 'deploy', true).flush(searchResponse({
        query: 'deploy',
        semanticUsed: true,
        expandedTerms: ['rollout', 'release'],
        results: [
          ...searchResponse().results,
          {
            relPath: 'ops/rollout.md', title: 'Rollout', kind: 'md',
            snippet: '<em>Rollout</em> Schritte', score: 0.7, updatedAt: null,
          },
        ],
      }));
      fixture.detectChanges();

      const terms = el(fixture).querySelector('[data-testid="wiki-search-expanded-terms"]')!;
      expect(terms, 'expanded terms').toBeTruthy();
      expect(terms.textContent).toContain('rollout');
      expect(terms.textContent).toContain('release');
      expect(el(fixture).querySelector('[data-testid="wiki-search-open-ops/rollout.md"]')).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
    http.verify();
  });

  it('hints when semantic expansion is unavailable (semanticUsed=false)', async () => {
    const { fixture, http } = await setup();
    const cmp = fixture.componentInstance;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      cmp.onSearchQueryChange('deploy');
      vi.advanceTimersByTime(300);
      searchRequest(http, 'deploy').flush(searchResponse({ query: 'deploy' }));
      fixture.detectChanges();

      el(fixture).querySelector<HTMLButtonElement>('[data-testid="wiki-search-semantic"]')!.click();
      fixture.detectChanges();
      searchRequest(http, 'deploy', true).flush(searchResponse({ query: 'deploy', semanticUsed: false }));
      fixture.detectChanges();

      expect(el(fixture).querySelector('[data-testid="wiki-search-semantic-unavailable"]')!.textContent)
        .toContain('Semantische Erweiterung nicht verfügbar');
    } finally {
      vi.useRealTimers();
    }
    http.verify();
  });

  // ---- curated landing links ("Einstiege") ----

  it('renders the Einstiege block on top of the landing and routes its links', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    const landing = root.querySelector('[data-testid="project-wiki-viewer-empty"]')!;
    const home = landing.querySelector('[data-testid="wiki-home-links"]')!;
    expect(home, 'Einstiege block').toBeTruthy();
    // The block sits at the very top of the landing article, above the Pulse head.
    expect(landing.firstElementChild!.contains(home)).toBe(true);
    expect(home.textContent).toContain('Einstiege');
    expect(home.textContent).toContain('Start');
    expect(home.textContent).toContain('Betrieb');
    // Note renders as a dimmed second line.
    expect(home.textContent).toContain('Der Einstieg');
    // Pulse feed, drift, and inbox remain below.
    expect(root.querySelector('[data-testid="project-wiki-pulse-feed"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-pulse-drift"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-pulse-inbox"]')).toBeTruthy();

    // A dangling link is dimmed and never navigates.
    const missing = home.querySelector<HTMLElement>('[data-testid="wiki-home-link-missing/gone.md"]')!;
    expect(missing.classList.contains('whome__link--missing')).toBe(true);
    missing.click();
    fixture.detectChanges();
    http.expectNone(r => r.url.includes('/wiki/files/'));

    // An existing link opens the page via the normal reader flow.
    home.querySelector<HTMLButtonElement>('[data-testid="wiki-home-link-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('concepts/overview.md');
    http.verify();
  });

  // ---- starred documents ("Gestarrt") ----

  it('stars the open page from the viewer head and lists it on the landing until unstarred', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    // Landing without stars: no Gestarrt section at all.
    expect(root.querySelector('[data-testid="project-wiki-starred"]')).toBeNull();

    // Open a page; the viewer-head toggle starts unstarred.
    expandConcepts(fixture);
    root.querySelector<HTMLElement>('[data-testid="project-wiki-file-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();

    const toggle = () => root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-star-toggle"]')!;
    expect(toggle(), 'viewer-head star toggle').toBeTruthy();
    expect(toggle().getAttribute('aria-pressed')).toBe('false');
    toggle().click();
    fixture.detectChanges();
    expect(toggle().getAttribute('aria-pressed')).toBe('true');

    // Back on the landing: the Gestarrt block sits above the Einstiege block and
    // shows label + dimmed relPath.
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-close"]')!.click();
    fixture.detectChanges();
    flushStyleGuidesIfRendered(http);
    flushWikiHomeIfRendered(http);
    fixture.detectChanges();
    const landing = root.querySelector('[data-testid="project-wiki-viewer-empty"]')!;
    const starred = landing.querySelector('[data-testid="project-wiki-starred"]')!;
    expect(starred, 'Gestarrt block').toBeTruthy();
    expect(landing.firstElementChild!.contains(starred)).toBe(true);
    expect(starred.textContent).toContain('Gestarrt');
    expect(starred.textContent).toContain('Concept overview');
    expect(starred.querySelector('code')!.textContent).toContain('concepts/overview.md');

    // Clicking the entry re-opens the page through the normal reader flow.
    starred.querySelector<HTMLButtonElement>('[data-testid="project-wiki-starred-open-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/concepts/overview.md')
      .flush({ relPath: 'concepts/overview.md', content: '# Hello wiki\n' });
    http.expectOne('/api/projects/Demo/wiki/history/concepts/overview.md').flush(HISTORY);
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('concepts/overview.md');
    expect(toggle().getAttribute('aria-pressed')).toBe('true');

    // Unstar directly at the landing entry: the whole section disappears.
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-close"]')!.click();
    fixture.detectChanges();
    flushStyleGuidesIfRendered(http);
    flushWikiHomeIfRendered(http);
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-starred-remove-concepts/overview.md"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-starred"]')).toBeNull();
    expect(localStorage.getItem('atp.projectWikiStars.v1.Demo')).toBeNull();
    http.verify();
  });

  // ---- Engineering Workstream frame (immutable) ----

  const FRAME_TREE: WikiTree = {
    projectName: 'Demo',
    baseDir: '/repo/docs',
    exists: true,
    root: [
      {
        // Backend relabels the frame root to "Workstream" (folder stays
        // engineering-workstream) and pins it first - see ProjectDocsService /
        // EngineeringWorkstreamFrame.DisplayTitle. The tree the component renders
        // therefore already carries the display title.
        name: 'engineering-workstream', title: 'Workstream',
        relPath: 'engineering-workstream', type: 'folder', immutable: true, children: [
          {
            name: '40-decision-log', title: 'decision-log',
            relPath: 'engineering-workstream/40-decision-log', type: 'folder', immutable: true, children: [
              {
                name: 'index.html', title: 'Decision Log',
                relPath: 'engineering-workstream/40-decision-log/index.html', type: 'html', immutable: true, children: [],
              },
              {
                name: 'adr-0001.md', title: 'ADR 1',
                relPath: 'engineering-workstream/40-decision-log/adr-0001.md', type: 'md', immutable: false, children: [],
              },
            ],
          },
          {
            name: '00-overview.html', title: 'Workstream',
            relPath: 'engineering-workstream/00-overview.html', type: 'html', immutable: true, children: [],
          },
        ],
      },
    ],
  };

  function expandFrame(fixture: { componentInstance: ProjectWikiSectionComponent; detectChanges: () => void }): void {
    fixture.componentInstance.toggleExpand('engineering-workstream');
    fixture.componentInstance.toggleExpand('engineering-workstream/40-decision-log');
    fixture.detectChanges();
  }

  it('renders the frame root labelled "Workstream" as the first tree node', async () => {
    const { fixture, http } = await setup(FRAME_TREE);
    const root = el(fixture);

    // The frame root node carries the relabelled display title...
    const frameRow = root.querySelector<HTMLElement>(
      '[data-testid="project-wiki-node-engineering-workstream"]');
    expect(frameRow, 'frame root row').toBeTruthy();
    expect(frameRow!.querySelector('.pwiki__label-text')!.textContent!.trim())
      .toBe('Workstream');

    // ...and it is the first top-level row rendered (pinned to the top).
    const firstRow = root.querySelector<HTMLElement>('[data-testid^="project-wiki-node-"]');
    expect(firstRow!.getAttribute('data-testid'))
      .toBe('project-wiki-node-engineering-workstream');
    http.verify();
  });

  it('marks frame folders and shells with a lock affordance, subpages without', async () => {
    const { fixture, http } = await setup(FRAME_TREE);
    expandFrame(fixture);
    const root = el(fixture);

    expect(root.querySelector('[data-testid="project-wiki-lock-engineering-workstream"]'), 'frame root lock').toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-lock-engineering-workstream/40-decision-log"]'), 'area lock').toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-lock-engineering-workstream/40-decision-log/index.html"]'), 'shell lock').toBeTruthy();
    expect(root.querySelector('[data-testid="project-wiki-lock-engineering-workstream/40-decision-log/adr-0001.md"]'), 'subpage lock absent').toBeNull();
    http.verify();
  });

  it('offers subpage creation on a frame area but hides rename/delete; a frame shell is history-only', async () => {
    const { fixture, http } = await setup(FRAME_TREE);
    expandFrame(fixture);
    const root = el(fixture);

    const openCtx = (id: string) => {
      const rowEl = root.querySelector<HTMLElement>(`[data-testid="project-wiki-node-${id}"]`);
      expect(rowEl, `row ${id}`).toBeTruthy();
      rowEl!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 40, clientY: 40 }));
      fixture.detectChanges();
    };

    // Frame area folder: subpages allowed, structure locked.
    openCtx('engineering-workstream/40-decision-log');
    expect(document.querySelector('[data-testid="wiki-ctx-item-new-page"]'), 'new page').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-new-folder"]'), 'new folder').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-rename"]'), 'no rename').toBeNull();
    expect(document.querySelector('[data-testid="wiki-ctx-item-delete"]'), 'no delete').toBeNull();

    fixture.componentInstance.closeMenu();
    fixture.detectChanges();

    // Frame landing shell: read-only, history only.
    openCtx('engineering-workstream/40-decision-log/index.html');
    expect(document.querySelector('[data-testid="wiki-ctx-item-history"]'), 'history').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-rename"]'), 'no rename').toBeNull();
    expect(document.querySelector('[data-testid="wiki-ctx-item-delete"]'), 'no delete').toBeNull();

    fixture.componentInstance.closeMenu();
    fixture.detectChanges();

    // A regular subpage under the area keeps the full menu.
    openCtx('engineering-workstream/40-decision-log/adr-0001.md');
    expect(document.querySelector('[data-testid="wiki-ctx-item-rename"]'), 'subpage rename').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-delete"]'), 'subpage delete').toBeTruthy();
    http.verify();
  });

  /** History payload for a frame shell (no git metadata, no commits). */
  function flushFrameHistory(http: HttpTestingController, rel: string): void {
    http.expectOne(`/api/projects/Demo/wiki/history/${rel}`).flush({
      relPath: rel, model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: null, status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    });
  }

  it('navigates between frame landing shells, rendering each in the interactive isolated iframe', async () => {
    const { fixture, http } = await setup(FRAME_TREE);
    expandFrame(fixture);
    const root = el(fixture);

    // Open the frame overview shell.
    root.querySelector<HTMLButtonElement>(
      '[data-testid="project-wiki-file-engineering-workstream/00-overview.html"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/engineering-workstream/00-overview.html')
      .flush({
        relPath: 'engineering-workstream/00-overview.html',
        content: '<!doctype html><html><body><h1>The development story</h1>'
          + '<section class="ew-grid">Current Development State</section></body></html>',
      });
    flushFrameHistory(http, 'engineering-workstream/00-overview.html');
    fixture.detectChanges();

    let frame = root.querySelector<HTMLIFrameElement>('[data-testid="project-wiki-html-frame"]');
    expect(frame, 'overview iframe').toBeTruthy();
    expect(frame!.getAttribute('sandbox')).toBe('allow-scripts');
    expect(frame!.getAttribute('sandbox')).not.toContain('allow-same-origin');
    expect(frame!.getAttribute('srcdoc') ?? frame!.srcdoc).toContain('The development story');
    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('engineering-workstream/00-overview.html');

    // Navigate on to the Decision Log area landing shell — the viewer switches
    // to the new frame page and re-renders the sandboxed iframe with its content.
    root.querySelector<HTMLButtonElement>(
      '[data-testid="project-wiki-file-engineering-workstream/40-decision-log/index.html"]')!.click();
    fixture.detectChanges();
    http.expectOne('/api/projects/Demo/wiki/files/engineering-workstream/40-decision-log/index.html')
      .flush({
        relPath: 'engineering-workstream/40-decision-log/index.html',
        content: '<!doctype html><html><body><h1>Decision Log</h1>'
          + '<div class="ew-rail"><span class="ew-pill ew-pill--here">04</span></div></body></html>',
      });
    flushFrameHistory(http, 'engineering-workstream/40-decision-log/index.html');
    fixture.detectChanges();

    frame = root.querySelector<HTMLIFrameElement>('[data-testid="project-wiki-html-frame"]');
    expect(frame!.getAttribute('srcdoc') ?? frame!.srcdoc).toContain('Decision Log');
    expect(frame!.getAttribute('srcdoc') ?? frame!.srcdoc).toContain('ew-pill--here');
    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent)
      .toContain('engineering-workstream/40-decision-log/index.html');
    http.verify();
  });
});

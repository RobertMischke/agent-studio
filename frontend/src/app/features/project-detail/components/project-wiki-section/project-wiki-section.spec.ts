import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectWikiSectionComponent } from './project-wiki-section';
import type { WikiFileHistory, WikiPulse, WikiTree } from '../../../../models/project-docs.model';

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
        },
        { name: 'page.html', title: 'HTML page', relPath: 'concepts/page.html', type: 'html', children: [] },
        { name: 'page.metadata.json', title: 'Page metadata', relPath: 'concepts/page.metadata.json', type: 'json', children: [] },
      ],
    },
    { name: 'README.md', title: 'Docs index', relPath: 'README.md', type: 'md', children: [] },
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
};

async function setup(tree: WikiTree = TREE) {
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
  flushWikiPulse(http);
  fixture.detectChanges();
  return { fixture, http };
}

/**
 * refresh() fetches the git-backed Pulse landing view alongside the tree. Every
 * refresh (initial load and post-mutation re-read) issues it, so the fake
 * backend must answer it or verify() trips on the dangling request.
 */
function flushWikiPulse(http: HttpTestingController, pulse: WikiPulse = PULSE): void {
  http.expectOne(r => r.url.includes('/wiki/pulse')).flush(pulse);
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
    if (key.startsWith('atp.projectWiki.v1.')) localStorage.removeItem(key);
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
    expect(reportFrame!.getAttribute('sandbox')).toBe('');
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

    root.querySelector<HTMLButtonElement>('[data-testid="project-wiki-toggle-context"]')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="project-wiki-workspace-meta"]')).toBeNull();
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
    expect(root.querySelector('[data-testid="project-wiki-meta-panel"]')).toBeNull();
    expect(root.querySelector('[data-testid="project-wiki-source-editor"]')!.textContent).toContain('# Restored');
    expect(root.querySelector('[data-testid="project-wiki-viewer-path"]')!.textContent).toContain('concepts/overview.md');
    expect(fixture.componentInstance.navWidth()).toBe(340);
    expect(fixture.componentInstance.contextWidth()).toBe(360);
    expect(JSON.parse(localStorage.getItem(wikiStorageKey()) ?? '{}').expandedIds).toContain('concepts');
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

  it('renders an HTML doc inside a script-disabled sandboxed iframe', async () => {
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
    // sandbox attribute is present and empty => no allow-scripts token.
    expect(frame!.getAttribute('sandbox')).toBe('');
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

  it('navigates between frame landing shells, rendering each in the script-disabled iframe', async () => {
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
    expect(frame!.getAttribute('sandbox')).toBe(''); // no allow-scripts token
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

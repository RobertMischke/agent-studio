import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectWikiSectionComponent } from './project-wiki-section';
import type { WikiFileHistory, WikiTree } from '../../../../models/project-docs.model';

const TREE: WikiTree = {
  projectName: 'Demo',
  baseDir: '/repo/docs',
  exists: true,
  root: [
    {
      name: 'concepts', title: 'concepts', relPath: 'concepts', type: 'folder', children: [
        { name: 'overview.md', title: 'Concept overview', relPath: 'concepts/overview.md', type: 'md', children: [] },
        { name: 'page.html', title: 'HTML page', relPath: 'concepts/page.html', type: 'html', children: [] },
      ],
    },
    { name: 'README.md', title: 'Docs index', relPath: 'README.md', type: 'md', children: [] },
  ],
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
  fixture.detectChanges();
  return { fixture, http };
}

const el = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

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
  return {
    clientX,
    pointerId,
    preventDefault() {},
    currentTarget: {
      setPointerCapture() {},
      releasePointerCapture() {},
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

  it('renders the physical folder tree with folders and md/html files', async () => {
    const { fixture, http } = await setup();
    const text = el(fixture).textContent ?? '';
    // Folders expand by default (seed effect), so children are visible.
    expect(text).toContain('concepts');
    expect(text).toContain('Concept overview');
    expect(text).toContain('Docs index');
    expect(text).toContain('3 pages'); // overview.md + page.html + README.md
    // The nav exposes the physical root path, parent path, and subtle file type icon.
    expect(el(fixture).querySelector('[data-testid="project-wiki-root-path"]')!.textContent)
      .toContain('/repo/docs');
    expect(el(fixture).querySelector('[data-testid="project-wiki-file-path-concepts/page.html"]')!.textContent)
      .toContain('concepts');
    expect(el(fixture).querySelector('[data-file-type="html"]')).toBeTruthy();
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

    // History is no longer a document tab; it lives in the right context rail.
    const text = el(fixture).textContent ?? '';
    expect(el(fixture).querySelector('[data-testid="project-wiki-history-panel"]')).toBeTruthy();
    expect(text).toContain('Claude Opus 4.8');
    expect(text).toContain('abc1234');
    http.verify();
  });

  it('shows last-modified (date, author, commit subject) in the doc header from git metadata', async () => {
    const { fixture, http } = await setup();
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

  it('renders a functional default workspace page and collapses side panels', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    expect(root.querySelector('[data-testid="project-wiki-viewer-empty"]')!.textContent)
      .toContain('Root folder');
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

  it('restores collapsed panels, selected document, and active tab from localStorage', async () => {
    localStorage.setItem(wikiStorageKey(), JSON.stringify({
      navCollapsed: true,
      contextCollapsed: true,
      openedRel: 'concepts/overview.md',
      viewerTab: 'source',
      navWidth: 340,
      contextWidth: 360,
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
    fixture.detectChanges();
    http.verify();
  });

  it('previews an old revision from the history panel', async () => {
    const { fixture, http } = await setup();
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
});

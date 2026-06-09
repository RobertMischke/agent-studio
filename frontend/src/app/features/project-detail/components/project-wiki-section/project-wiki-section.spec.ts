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

  it('renders a nested group hierarchy with increasing indentation per depth', async () => {
    const { fixture, http } = await setup({
      version: 1,
      nodes: [
        { id: 'g1', type: 'group', title: 'Architecture', relPath: null, parentId: null, order: 0 },
        { id: 'g2', type: 'group', title: 'Decisions', relPath: null, parentId: 'g1', order: 0 },
        { id: 'doc:concepts/overview.md', type: 'doc', title: null, relPath: 'concepts/overview.md', parentId: 'g2', order: 0 },
      ],
    });

    const root = el(fixture);
    const pad = (id: string): number => {
      const row = root.querySelector<HTMLElement>(`[data-testid="project-wiki-node-${id}"]`);
      expect(row, `row ${id} should render`).toBeTruthy();
      return parseFloat(row!.style.paddingLeft || '0');
    };

    // Tree is g1 > g2 > doc; each level indents deeper than its parent, proving
    // the nesting is actually rendered (not a flattened list).
    const g1Pad = pad('g1');
    const g2Pad = pad('g2');
    const docPad = pad('doc:concepts/overview.md');
    expect(g2Pad).toBeGreaterThan(g1Pad);
    expect(docPad).toBeGreaterThan(g2Pad);

    // The nested group label is visible (parent expanded by the seed effect).
    expect(root.querySelector('[data-testid="project-wiki-node-g2"]')!.textContent).toContain('Decisions');
    http.verify();
  });

  it('opens a TEXT-ONLY right-click context menu (no icons) for docs and groups', async () => {
    const { fixture, http } = await setup({
      version: 1,
      nodes: [
        { id: 'g1', type: 'group', title: 'Concepts', relPath: null, parentId: null, order: 0 },
      ],
    });
    const root = el(fixture);

    const openCtx = (id: string) => {
      const row = root.querySelector<HTMLElement>(`[data-testid="project-wiki-node-${id}"]`);
      expect(row, `row ${id}`).toBeTruthy();
      row!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 40, clientY: 40 }));
      fixture.detectChanges();
    };

    const assertTextOnly = (panel: HTMLElement) => {
      // The project-wide menu convention: no decorative leading icons.
      expect(panel.querySelectorAll('img')).toHaveLength(0);
      expect(panel.querySelectorAll('svg')).toHaveLength(0);
      expect(panel.querySelectorAll('.app-menu__icon')).toHaveLength(0);
    };

    // The menu renders into a document-level overlay portal, so query globally.
    // Doc context menu: Rename + View history rows, text-only.
    openCtx('doc:README.md');
    let panel = document.querySelector<HTMLElement>('[data-testid="wiki-ctx-panel"]');
    expect(panel, 'doc context menu panel').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-rename"]')!.textContent).toContain('Rename');
    expect(document.querySelector('[data-testid="wiki-ctx-item-history"]')).toBeTruthy();
    assertTextOnly(panel!);

    fixture.componentInstance.closeMenu();
    fixture.detectChanges();

    // Group context menu: Rename + New subgroup + Delete group, also text-only.
    openCtx('g1');
    panel = document.querySelector<HTMLElement>('[data-testid="wiki-ctx-panel"]');
    expect(panel, 'group context menu panel').toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-rename"]')).toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-subgroup"]')).toBeTruthy();
    expect(document.querySelector('[data-testid="wiki-ctx-item-delete"]')!.textContent).toContain('Delete');
    assertTextOnly(panel!);
    http.verify();
  });

  it('drag-drops a doc onto a group and persists the new parent in the manifest', async () => {
    const { fixture, http } = await setup({
      version: 1,
      nodes: [
        { id: 'g1', type: 'group', title: 'Concepts', relPath: null, parentId: null, order: 0 },
      ],
    });
    const root = el(fixture);

    // README starts in the Ungrouped bucket; drag it onto the real group g1.
    const docRow = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-doc:README.md"]');
    const groupRow = root.querySelector<HTMLElement>('[data-testid="project-wiki-node-g1"]');
    expect(docRow).toBeTruthy();
    expect(groupRow).toBeTruthy();

    const dt = makeDataTransfer();
    fireDrag(docRow!, 'dragstart', dt);
    fireDrag(groupRow!, 'dragover', dt);
    fireDrag(groupRow!, 'drop', dt);
    fixture.detectChanges();

    const put = http.expectOne(req =>
      req.method === 'PUT' && req.url === '/api/projects/Demo/wiki/organization');
    const body = put.request.body as WikiOrganization;
    const moved = body.nodes.find(n => n.type === 'doc' && n.relPath === 'README.md');
    expect(moved, 'README should be pinned into the manifest').toBeTruthy();
    expect(moved!.parentId).toBe('g1');
    put.flush(body);
    fixture.detectChanges();
    http.verify();
  });
});

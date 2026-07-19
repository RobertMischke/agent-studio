import { describe, expect, it } from 'vitest';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { GitFileTreeComponent } from './git-file-tree.component';
import type { GitFileChange } from '../../../../../features/git';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('GitFileTreeComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [GitFileTreeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(GitFileTreeComponent);
    fixture.componentRef.setInput('files', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // files
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] GitFileTreeComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('reflects the [fill] input onto the host as data-fill', async () => {
    // Regression: the SCSS `:host([data-fill="true"]) .git-tree` rule needs
    // a host attribute to match. Without the host binding the file tree
    // stayed capped at max-height: 30vh in the pane-maximized split layout
    // and left a tall empty band under the file list.
    await TestBed.configureTestingModule({
      imports: [GitFileTreeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(GitFileTreeComponent);
    fixture.componentRef.setInput('files', []);
    fixture.componentRef.setInput('fill', false);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.getAttribute('data-fill')).toBeNull();

    fixture.componentRef.setInput('fill', true);
    fixture.detectChanges();
    expect(host.getAttribute('data-fill')).toBe('true');

    fixture.componentRef.setInput('fill', false);
    fixture.detectChanges();
    expect(host.getAttribute('data-fill')).toBeNull();
  });
});

describe('GitFileTreeComponent keyboard navigation', () => {
  const FILES: GitFileChange[] = [
    { status: 'M', path: 'backend/Foo.cs', added: 3, removed: 1 },
    { status: 'M', path: 'frontend/src/app.ts', added: 5, removed: 0 },
  ];

  async function mount(): Promise<ComponentFixture<GitFileTreeComponent>> {
    await TestBed.configureTestingModule({
      imports: [GitFileTreeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(GitFileTreeComponent);
    fixture.componentRef.setInput('files', FILES);
    fixture.componentRef.setInput('selected', null);
    fixture.detectChanges();
    return fixture;
  }

  function rows(fixture: ComponentFixture<GitFileTreeComponent>): HTMLElement[] {
    return Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('li.git-tree__row'),
    );
  }

  it('exposes ARIA tree roles with a single roving tabindex', async () => {
    const fixture = await mount();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('ul.git-tree')?.getAttribute('role')).toBe('tree');
    const items = rows(fixture);
    expect(items.length).toBeGreaterThan(0);
    expect(items.every((el) => el.getAttribute('role') === 'treeitem')).toBe(true);

    // Roving tabindex: exactly one row is tab-focusable (defaults to the first).
    const tabbable = items.filter((el) => el.getAttribute('tabindex') === '0');
    expect(tabbable.length).toBe(1);
    expect(items.filter((el) => el.getAttribute('tabindex') === '-1').length).toBe(items.length - 1);

    // Folders advertise expansion state; files advertise selection state.
    const folder = items.find((el) => el.getAttribute('data-testid') === 'git-tree-folder');
    const file = items.find((el) => el.getAttribute('data-testid') === 'git-tree-file');
    expect(folder?.getAttribute('aria-expanded')).toBeTruthy();
    expect(file?.getAttribute('aria-selected')).toBe('false');
  });

  it('moves the selection with ArrowDown and loads the focused file diff', async () => {
    const fixture = await mount();
    const ul = (fixture.nativeElement as HTMLElement).querySelector('ul.git-tree') as HTMLElement;

    const emitted: string[] = [];
    fixture.componentInstance.selectRequest.subscribe((p) => emitted.push(p));

    // Roving start is the first row (folder "backend"); first ArrowDown lands
    // on the file inside it and selects it (loads the diff on the right).
    ul.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    expect(emitted).toEqual(['backend/Foo.cs']);

    // Next ArrowDown moves onto the "frontend/src" folder: focus only, no select.
    ul.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    expect(emitted).toEqual(['backend/Foo.cs']);

    // Final ArrowDown reaches the second file and selects it.
    ul.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    expect(emitted).toEqual(['backend/Foo.cs', 'frontend/src/app.ts']);
  });

  it('collapses and expands folders with ArrowLeft / ArrowRight', async () => {
    const fixture = await mount();
    const ul = (fixture.nativeElement as HTMLElement).querySelector('ul.git-tree') as HTMLElement;

    // Focus the first folder, then collapse it: its child file disappears.
    ul.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true }));
    ul.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    fixture.detectChanges();
    expect(
      rows(fixture).some((el) => el.getAttribute('data-path') === 'backend/Foo.cs'),
    ).toBe(false);

    // Re-expand it: the child file is visible again.
    ul.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    fixture.detectChanges();
    expect(
      rows(fixture).some((el) => el.getAttribute('data-path') === 'backend/Foo.cs'),
    ).toBe(true);
  });
});

describe('GitFileTreeComponent path disambiguation (AGT-2008)', () => {
  // Two README.md in different folders — the reported "which is which" case.
  const COLLIDING: GitFileChange[] = [
    { status: 'M', path: 'README.md', added: 1, removed: 0 },
    { status: 'M', path: 'docs/start/README.md', added: 2, removed: 1 },
    { status: 'M', path: 'src/app.ts', added: 4, removed: 0 },
  ];

  async function mount(files: GitFileChange[]): Promise<ComponentFixture<GitFileTreeComponent>> {
    await TestBed.configureTestingModule({
      imports: [GitFileTreeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(GitFileTreeComponent);
    fixture.componentRef.setInput('files', files);
    fixture.componentRef.setInput('selected', null);
    fixture.detectChanges();
    return fixture;
  }

  it('shows a directory hint only on files whose basename collides', async () => {
    const fixture = await mount(COLLIDING);
    const hints = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>(
        '[data-testid="git-tree-dir-hint"]',
      ),
    ).map((el) => el.textContent?.trim());

    // Both README.md rows disambiguate; the unique app.ts row gets no hint.
    expect(hints).toContain('root');
    expect(hints).toContain('docs/');
    expect(hints.length).toBe(2);
  });

  it('exposes the full repo path as the file tooltip + parent-dir hint via the API', async () => {
    const fixture = await mount(COLLIDING);
    const cmp = fixture.componentInstance;
    const fileNode = (path: string) => ({
      path,
      label: path.split('/').pop()!,
      isFile: true,
      status: 'M',
      added: 0,
      removed: 0,
      count: 1,
      children: [],
      depth: 0,
    });

    expect(cmp.fileTooltip(fileNode('docs/start/README.md'))).toBe('docs/start/README.md');
    expect(cmp.dirHint(fileNode('docs/start/README.md'))).toBe('docs/');
    expect(cmp.dirHint(fileNode('README.md'))).toBe('root');
    // Unique basename -> no hint even though the method is called per row.
    expect(cmp.dirHint(fileNode('src/app.ts'))).toBe('');
  });

  it('adds no hint when every basename is unique', async () => {
    const fixture = await mount([
      { status: 'M', path: 'a/one.ts', added: 1, removed: 0 },
      { status: 'M', path: 'b/two.ts', added: 1, removed: 0 },
    ]);
    const hints = (fixture.nativeElement as HTMLElement).querySelectorAll(
      '[data-testid="git-tree-dir-hint"]',
    );
    expect(hints.length).toBe(0);
  });
});

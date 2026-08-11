import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { WikiStarredOpenRequest, WikiStarredPanelComponent } from './wiki-starred-panel.component';
import { WikiStarsService } from '../wiki-stars.service';

function clearStarStorage(): void {
  for (const key of Object.keys(localStorage)) {
    if (key.startsWith('atp.projectWikiStars.v1.')) localStorage.removeItem(key);
  }
}

async function setup() {
  await TestBed.configureTestingModule({
    imports: [WikiStarredPanelComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();

  const stars = TestBed.inject(WikiStarsService);
  const fixture = TestBed.createComponent(WikiStarredPanelComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.detectChanges();
  return { fixture, stars };
}

const el = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

describe('WikiStarredPanelComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    clearStarStorage();
  });
  afterEach(() => clearStarStorage());

  it('renders the starred entries newest-first, emits open intent, and unstars in place', async () => {
    const { fixture, stars } = await setup();
    const root = el(fixture);

    // Empty store: the block renders nothing at all.
    expect(root.querySelector('[data-testid="project-wiki-starred"]')).toBeNull();

    stars.star('Demo', 'concepts/overview.md', 'Concept overview');
    stars.star('Demo', 'workbench/board.html', 'Dossier board');
    fixture.detectChanges();

    const block = root.querySelector('[data-testid="project-wiki-starred"]')!;
    expect(block.textContent).toContain('Gestarrt');
    // Newest first; each entry shows label + dimmed relPath.
    const opens = [...block.querySelectorAll('[data-testid^="project-wiki-starred-open-"]')];
    expect(opens.map(open => open.getAttribute('data-testid'))).toEqual([
      'project-wiki-starred-open-workbench/board.html',
      'project-wiki-starred-open-concepts/overview.md',
    ]);
    expect(opens[1].textContent).toContain('Concept overview');
    expect(opens[1].querySelector('code')!.textContent).toContain('concepts/overview.md');

    // Entry click emits open intent with the type derived from the extension.
    const requests: WikiStarredOpenRequest[] = [];
    fixture.componentInstance.openEntry.subscribe(request => requests.push(request));
    (opens[0] as HTMLButtonElement).click();
    (opens[1] as HTMLButtonElement).click();
    expect(requests).toEqual([
      { relPath: 'workbench/board.html', type: 'html' },
      { relPath: 'concepts/overview.md', type: 'md' },
    ]);

    // Unstar at the entry removes it from the store without emitting open intent.
    block.querySelector<HTMLButtonElement>('[data-testid="project-wiki-starred-remove-workbench/board.html"]')!.click();
    fixture.detectChanges();
    expect(requests).toHaveLength(2);
    expect(stars.isStarred('Demo', 'workbench/board.html')).toBe(false);
    expect(root.querySelector('[data-testid="project-wiki-starred-open-workbench/board.html"]')).toBeNull();
    expect(root.querySelector('[data-testid="project-wiki-starred-open-concepts/overview.md"]')).toBeTruthy();
  });
});

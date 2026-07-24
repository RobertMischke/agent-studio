import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { PromptCatalogItem } from '../../../../services/prompt-admin.service';
import { PromptCatalogueNavComponent } from './prompt-catalogue-nav.component';

const items: PromptCatalogItem[] = [
  {
    name: 'review-aspect-code-quality.md',
    title: 'Code quality',
    description: 'Review code.',
    group: 'Review',
    hasDefault: true,
    hasGlobalOverride: false,
    hasOverride: true,
    globalDefaultChangedSinceOverride: false,
    defaultChangedSinceOverride: true,
    slots: [],
    usageCount: 1,
    projectOverrides: [
      {
        projectName: 'Agent Studio',
        stepId: 'aspect-code-quality',
        promptName: 'review-aspect-code-quality.md',
        content: 'custom',
        orphaned: false,
        matchesDefault: false,
        addedLines: 1,
        removedLines: 1,
        baseDefaultSha: '9a388acf',
        defaultChangedSinceOverride: true,
      },
      {
        projectName: 'Marketing Site',
        stepId: 'aspect-code-quality',
        promptName: 'review-aspect-code-quality.md',
        content: 'marketing custom',
        orphaned: false,
        matchesDefault: false,
        addedLines: 2,
        removedLines: 1,
        baseDefaultSha: '2cfd8ede',
        defaultChangedSinceOverride: false,
      },
    ],
  },
  {
    name: 'runner-fresh-start.md',
    title: 'Fresh start',
    description: 'Start a run.',
    group: 'Runner',
    hasDefault: true,
    hasOverride: false,
    defaultChangedSinceOverride: false,
    slots: [],
    usageCount: 1,
  },
];

describe('PromptCatalogueNavComponent', () => {
  it('shows project origin and stale warning beside the shared override badge', async () => {
    await TestBed.configureTestingModule({
      imports: [PromptCatalogueNavComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PromptCatalogueNavComponent);
    fixture.componentRef.setInput('items', items);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="prompt-admin-override-review-aspect-code-quality.md"]')
      ?.textContent).toContain('overridden - Agent Studio');
    expect(host.querySelector('[data-testid="prompt-admin-stale-review-aspect-code-quality.md"]')
      ?.getAttribute('aria-label')).toBe('Override based on outdated shipped default - review needed');
  });

  it('filters by scoped overrides without duplicating the catalogue tree', async () => {
    await TestBed.configureTestingModule({
      imports: [PromptCatalogueNavComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PromptCatalogueNavComponent);
    fixture.componentRef.setInput('items', items);
    fixture.componentRef.setInput('projectName', 'Other Project');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('0 overridden');
    host.querySelector<HTMLButtonElement>('[data-testid="prompt-admin-only-overrides"]')!.click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid^="prompt-admin-item-"]')).toBeNull();
  });

  it('lists every project origin in workspace scope', async () => {
    await TestBed.configureTestingModule({
      imports: [PromptCatalogueNavComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PromptCatalogueNavComponent);
    fixture.componentRef.setInput('items', items);
    fixture.detectChanges();

    const origin = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="prompt-admin-override-review-aspect-code-quality.md"]',
    );
    expect(origin?.textContent).toContain('overridden - Agent Studio, Marketing Site');
  });
});

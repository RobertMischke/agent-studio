import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';

import { PromptAdminLandingComponent } from './prompt-admin-landing.component';

describe('PromptAdminLandingComponent', () => {
  it('renders the four prompt classes and opens a prompt link', async () => {
    await TestBed.configureTestingModule({
      imports: [PromptAdminLandingComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PromptAdminLandingComponent);
    fixture.componentRef.setInput('items', [
      {
        name: 'runner-fresh-start.md',
        title: 'Runner: fresh start',
        description: 'Starts a run.',
        group: 'Runner',
        promptClass: 'runtime-step',
        hasDefault: true,
        hasOverride: false,
        defaultChangedSinceOverride: false,
        slots: [],
        usageCount: 1,
        lastChangedAt: null,
        lastChangedSha: null,
        lastReviewedAt: null,
        reviewStatus: null,
        reviewFindingCount: 0,
        projectOverrideCount: 0,
        calls: {
          totalCalls: 4,
          calls7d: 2,
          lastCalledAt: '2026-07-23T10:00:00Z',
          inputTokens: 400,
          costUsd: 0.001,
          costUsd7d: 0.0005,
          unpricedCalls: 0,
          unpricedCalls7d: 0,
          currentVersionCalls: 4,
          isDead: false,
          daily: [{ date: '2026-07-23', calls: 2, inputTokens: 200, costUsd: 0.0005 }],
          versions: [],
        },
      },
    ]);
    const selected: string[] = [];
    fixture.componentInstance.openPrompt.subscribe(name => selected.push(name));
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelectorAll('[data-testid^="prompt-admin-class-"]').length).toBe(4);
    expect(host.querySelector('[data-testid="prompt-admin-overview-table"]')).not.toBeNull();
    host.querySelector<HTMLButtonElement>(
      '[data-testid="prompt-admin-landing-link-runner-fresh-start.md"]'
    )!.click();
    expect(selected).toEqual(['runner-fresh-start.md']);
  });
});

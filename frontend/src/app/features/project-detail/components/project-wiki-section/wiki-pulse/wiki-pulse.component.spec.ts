import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { WikiPulseComponent, WikiPulseOpenRequest } from './wiki-pulse.component';
import type { WikiPulse } from '../../../../../models/project-docs.model';

function isoDaysAgo(days: number, hour = 9): string {
  const d = new Date();
  d.setDate(d.getDate() - days);
  d.setHours(hour, 0, 0, 0);
  return d.toISOString();
}

function makePulse(overrides: Partial<WikiPulse> = {}): WikiPulse {
  return {
    projectName: 'Demo',
    baseDir: '/repo/docs',
    exists: true,
    generatedAtUtc: new Date().toISOString(),
    feed: {
      available: true,
      reason: null,
      items: [
        {
          relPath: 'engineering-workstream/10-current-development-state/active.md',
          title: 'Active stream',
          author: 'Alice',
          authorDateUtc: isoDaysAgo(0),
          sha: 'a1',
          shortSha: 'a1',
          subject: 'AGT-2014 update',
          frameAreaSlug: '10-current-development-state',
          frameAreaTitle: 'Current Development State',
          taskKey: 'AGT-2014',
        },
        {
          relPath: 'notes.md',
          title: 'Notes',
          author: 'Bob',
          authorDateUtc: isoDaysAgo(1),
          sha: 'b1',
          shortSha: 'b1',
          subject: 'jot notes',
          frameAreaSlug: null,
          frameAreaTitle: null,
          taskKey: null,
        },
      ],
    },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: {
      available: true,
      reason: null,
      overallGrade: 'Stale',
      areas: [
        { slug: '10-current-development-state', title: 'Current Development State', grade: 'Stale', pageCount: 1, gradedPageCount: 1, worstCommitCount: 60, freshCount: 0, agingCount: 0, staleCount: 1 },
        { slug: '20-development-signals', title: 'Development Signals', grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 },
      ],
      counts: { fresh: 0, aging: 0, stale: 1, graded: 1 },
    },
    ...overrides,
  };
}

async function mount(pulse: WikiPulse | null) {
  await TestBed.configureTestingModule({
    imports: [WikiPulseComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(WikiPulseComponent);
  fixture.componentRef.setInput('pulse', pulse);
  fixture.detectChanges();
  return fixture;
}

const html = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

describe('WikiPulseComponent', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('groups the change feed by local day (Today / Yesterday)', async () => {
    const fixture = await mount(makePulse());
    const heads = Array.from(html(fixture).querySelectorAll('.wpulse__day-head')).map(e => e.textContent?.trim());
    expect(heads).toContain('Today');
    expect(heads).toContain('Yesterday');
  });

  it('renders the frame-area badge and the task key on a feed row', async () => {
    const fixture = await mount(makePulse());
    const root = html(fixture);
    const badge = root.querySelector('[data-testid="project-wiki-pulse-area-badge-engineering-workstream/10-current-development-state/active.md"]');
    expect(badge?.textContent).toContain('Current Development State');
    const task = root.querySelector('[data-testid="project-wiki-pulse-task-engineering-workstream/10-current-development-state/active.md"]');
    expect(task?.textContent).toContain('AGT-2014');
  });

  it('maps drift grades to tones on the grade bar', async () => {
    const fixture = await mount(makePulse());
    const root = html(fixture);
    const stale = root.querySelector('[data-testid="project-wiki-pulse-area-10-current-development-state"]');
    expect(stale?.getAttribute('data-tone')).toBe('bad');
    const empty = root.querySelector('[data-testid="project-wiki-pulse-area-20-development-signals"]');
    expect(empty?.getAttribute('data-tone')).toBe('muted');
  });

  it('emits openPage when a feed row is clicked, deriving the node type', async () => {
    const fixture = await mount(makePulse());
    let emitted: WikiPulseOpenRequest | null = null;
    fixture.componentInstance.openPage.subscribe(r => (emitted = r));
    html(fixture)
      .querySelector<HTMLButtonElement>('[data-testid="project-wiki-pulse-feed-open-notes.md"]')!
      .click();
    expect(emitted).toEqual({ relPath: 'notes.md', type: 'md' });
  });

  it('shows the healthy empty-inbox state when the inbox is clear', async () => {
    const fixture = await mount(makePulse({ inbox: { available: true, reason: null, count: 0, items: [] } }));
    expect(html(fixture).querySelector('[data-testid="project-wiki-pulse-inbox-empty"]')).toBeTruthy();
  });

  it('degrades each section to a reason when its source is unavailable', async () => {
    const fixture = await mount(makePulse({
      feed: { available: false, reason: 'No git repository resolved for this project.', items: [] },
      inbox: { available: false, reason: 'No docs/ folder for this project yet.', count: 0, items: [] },
      drift: { available: false, reason: 'No git repository resolved for this project.', overallGrade: 'Empty', areas: [], counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
    }));
    const root = html(fixture);
    expect(root.querySelector('[data-testid="project-wiki-pulse-feed-unavailable"]')?.textContent).toContain('No git repository');
    expect(root.querySelector('[data-testid="project-wiki-pulse-inbox-unavailable"]')?.textContent).toContain('No docs/ folder');
    expect(root.querySelector('[data-testid="project-wiki-pulse-drift-unavailable"]')).toBeTruthy();
  });

  it('shows the drift empty-state hint when no pages are graded', async () => {
    const fixture = await mount(makePulse({
      drift: {
        available: true,
        reason: 'No knowledge pages filed under the Workstream frame yet.',
        overallGrade: 'Empty',
        areas: [
          { slug: '10-current-development-state', title: 'Current Development State', grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 },
        ],
        counts: { fresh: 0, aging: 0, stale: 0, graded: 0 },
      },
    }));
    expect(html(fixture).querySelector('[data-testid="project-wiki-pulse-drift-empty"]')?.textContent)
      .toContain('No knowledge pages filed');
  });
});

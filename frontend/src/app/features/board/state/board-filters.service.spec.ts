import { describe, it, expect, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { BoardFiltersService } from './board-filters.service';
import { TaskService } from '../../../services/task.service';
import type { GroupedJobs, TaskInfo } from '../../../models/task.model';

/**
 * Regression test for the cross-project counter "leak": ensure that
 * selectProject() switches the active set rather than stacking entries,
 * and that filteredGrouped() reflects the change immediately. The bug
 * report claimed lane counters showed Agent Task Processor numbers even
 * after selecting Lotta Dashboard; the underlying cause was the chip
 * strip's pure-toggle behaviour leaving both projects active, so the
 * counts only crept by however many Lotta jobs exist.
 */

function makeJob(id: string, projectName: string, state: string): TaskInfo {
  return {
    id,
    taskKey: `${projectName}::${id}`,
    title: id,
    state,
    order: 0,
    watchPath: `wp/${projectName}`,
    projectName,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-01-01T00:00:00Z',
    lastActivity: null,
    execution: null,
  } as unknown as TaskInfo;
}

function makeGrouped(jobs: TaskInfo[]): GroupedJobs {
  const byState = (s: string) => jobs.filter((j) => j.state === s);
  return {
    backlog: byState('0-backlog'),
    preparation: byState('1-preparation'),
    orchestratorPrep: [],
    ready: byState('2-ready'),
    progress: byState('3-progress'),
    failedPickup: [],
    codeNotComplete: [],
    autoReview: byState('4-auto-review'),
    humanReview: byState('5-human-review'),
    review: byState('4-auto-review'),
    completed: byState('6-completed'),
    archive: byState('7-archive'),
  } as unknown as GroupedJobs;
}

describe('BoardFiltersService project selection', () => {
  let svc: BoardFiltersService;
  let jobs: TaskService;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    jobs = TestBed.inject(TaskService);
    svc = TestBed.inject(BoardFiltersService);

    const fixture: TaskInfo[] = [
      // Agent Task Processor - the loud project
      makeJob('atp-r-1', 'Agent Task Processor', '2-ready'),
      makeJob('atp-r-2', 'Agent Task Processor', '2-ready'),
      makeJob('atp-r-3', 'Agent Task Processor', '2-ready'),
      makeJob('atp-p-1', 'Agent Task Processor', '3-progress'),
      makeJob('atp-a-1', 'Agent Task Processor', '7-archive'),
      makeJob('atp-a-2', 'Agent Task Processor', '7-archive'),
      // Lotta Dashboard - the quiet project
      makeJob('lot-r-1', 'Lotta Dashboard', '2-ready'),
      makeJob('lot-a-1', 'Lotta Dashboard', '7-archive'),
    ];
    jobs.grouped.set(makeGrouped(fixture));
  });

  it('no filter shows every project', () => {
    const g = svc.filteredGrouped();
    expect(g.ready.length).toBe(4);
    expect(g.archive.length).toBe(3);
  });

  it('selectProject switches to single-project view by default', () => {
    svc.selectProject('Agent Task Processor', false);
    expect([...svc.activeProjects()]).toEqual(['Agent Task Processor']);
    let g = svc.filteredGrouped();
    expect(g.ready.length).toBe(3);
    expect(g.archive.length).toBe(2);

    // The reported bug: clicking Lotta while ATP active must REPLACE,
    // not add. Pre-fix this stacked and ready.length stayed at 4.
    svc.selectProject('Lotta Dashboard', false);
    expect([...svc.activeProjects()]).toEqual(['Lotta Dashboard']);
    g = svc.filteredGrouped();
    expect(g.ready.length).toBe(1);
    expect(g.archive.length).toBe(1);
  });

  it('selectProject with additive=true extends the active set (legacy multi-select)', () => {
    svc.selectProject('Agent Task Processor', false);
    svc.selectProject('Lotta Dashboard', true);
    expect(svc.activeProjects().has('Agent Task Processor')).toBe(true);
    expect(svc.activeProjects().has('Lotta Dashboard')).toBe(true);
    const g = svc.filteredGrouped();
    expect(g.ready.length).toBe(4);
    expect(g.archive.length).toBe(3);
  });

  it('selectProject on the sole-active chip clears the filter', () => {
    svc.selectProject('Agent Task Processor', false);
    svc.selectProject('Agent Task Processor', false);
    expect(svc.activeProjects().size).toBe(0);
    const g = svc.filteredGrouped();
    expect(g.ready.length).toBe(4);
  });

  it('filteredGrouped never mixes other projects when one is selected', () => {
    svc.selectProject('Lotta Dashboard', false);
    const g = svc.filteredGrouped();
    for (const lane of [g.ready, g.archive, g.progress, g.preparation, g.completed]) {
      for (const j of lane) {
        expect(j.projectName).toBe('Lotta Dashboard');
      }
    }
  });

  it('does not count project scope as an active filter', () => {
    svc.selectProject('Agent Task Processor', false);

    expect(svc.activeFilterCount()).toBe(0);
    expect(svc.hasActiveFilters()).toBe(false);
    expect(svc.hasActiveFiltersOrSearch()).toBe(false);
  });

  it('counts only real active filters and search text', () => {
    svc.selectProject('Agent Task Processor', false);
    svc.setSearchQuery('ready');
    svc.setClientFilter('owner-1');
    svc.onSetType('bug');
    svc.toggleTagFilter('important');

    expect(svc.activeFilterCount()).toBe(4);
    expect(svc.hasActiveFilters()).toBe(true);
    expect(svc.hasActiveFiltersOrSearch()).toBe(true);
  });
});

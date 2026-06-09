import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskSelectionService } from './task-selection.service';
import { TaskService } from '../../../services/task.service';
import { BoardFiltersService } from '../../board/state/board-filters.service';
import type { GroupedJobs, TaskInfo } from '../../../models/task.model';

/**
 * Single-source-of-truth contract for the lane-count badge vs the detail
 * pager total: the pager iterates `peersForLane`, the badge counts
 * `BoardFiltersService.filteredGrouped`. Both MUST resolve to the same
 * project-scoped, faceted-filtered task set so "N / M" never disagrees
 * with the lane header (the 116-vs-126 bug).
 */
describe('TaskSelectionService · lane peers honour the board scope (badge == pager)', () => {
  let selection: TaskSelectionService;
  let tasks: TaskService;
  let filters: BoardFiltersService;

  const makeJob = (id: string, project: string, state = '5-human-review'): TaskInfo =>
    ({
      id,
      taskKey: `${project}::${id}`,
      title: id,
      state,
      order: 1,
      watchPath: `/wp/${project}`,
      projectName: project,
    }) as unknown as TaskInfo;

  const emptyGrouped = (): GroupedJobs =>
    ({
      backlog: [],
      preparation: [],
      orchestratorPrep: [],
      ready: [],
      progress: [],
      failedPickup: [],
      codeNotComplete: [],
      review: [],
      autoReview: [],
      humanReview: [],
      escalated: [],
      completed: [],
      archive: [],
    }) as GroupedJobs;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    selection = TestBed.inject(TaskSelectionService);
    tasks = TestBed.inject(TaskService);
    filters = TestBed.inject(BoardFiltersService);
    // Reset any project filter leaked in from localStorage.
    filters.clearAllFilters();
  });

  it('scopes Review peers to the active project so the count matches the badge', () => {
    // A Review lane spanning two projects: alpha owns 3, beta owns 2.
    tasks.grouped.set({
      ...emptyGrouped(),
      humanReview: [
        makeJob('a1', 'alpha'),
        makeJob('b1', 'beta'),
        makeJob('a2', 'alpha'),
        makeJob('b2', 'beta'),
        makeJob('a3', 'alpha'),
      ],
    });

    // No filter: pager peers == full lane == badge.
    expect(selection.peersForLane('5-human-review').length).toBe(5);
    expect(filters.filteredGrouped().humanReview.length).toBe(5);

    // Scope to alpha: badge drops to 3 and the pager total must follow.
    filters.selectProject('alpha', false);
    const peers = selection.peersForLane('5-human-review');
    const badge = filters.filteredGrouped().humanReview.length;
    expect(badge).toBe(3);
    expect(peers.length).toBe(badge);
    expect(peers.every(j => j.projectName === 'alpha')).toBe(true);
  });

  it('applies the active type filter to lane peers as well', () => {
    const bug = (id: string, project: string) =>
      ({ ...makeJob(id, project), taskType: 'bug' }) as unknown as TaskInfo;
    const chore = (id: string, project: string) =>
      ({ ...makeJob(id, project), taskType: 'chore' }) as unknown as TaskInfo;
    tasks.grouped.set({
      ...emptyGrouped(),
      humanReview: [bug('a1', 'alpha'), chore('a2', 'alpha'), bug('a3', 'alpha')],
    });

    filters.onSetType('bug');
    const peers = selection.peersForLane('5-human-review');
    const badge = filters.filteredGrouped().humanReview.length;
    expect(badge).toBe(2);
    expect(peers.length).toBe(badge);
  });
});

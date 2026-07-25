import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import type { RegistryWorkspaceListItem, TaskDetail, TaskInfo } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import { ProjectLookupService } from '../../../services/project-lookup.service';
import { TaskSelectionService } from './task-selection.service';

describe('TaskSelectionService · stable task URLs', () => {
  let selection: TaskSelectionService;
  let tasks: TaskService;
  let projects: ProjectLookupService;
  let http: HttpTestingController;

  const info = {
    id: 'human-readable-slug',
    key: 'AGT-2124',
    displayKey: 'AGT-2124',
    taskKey: 'C:\\private\\project::human-readable-slug',
    title: 'Stable URL task',
    state: '5-human-review',
    order: 1,
    watchPath: 'C:\\private\\project',
    projectName: 'Agent Studio',
  } as unknown as TaskInfo;

  const detail = { info } as unknown as TaskDetail;

  beforeEach(async () => {
    sessionStorage.clear();
    history.replaceState(null, '', '/');
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
    projects = TestBed.inject(ProjectLookupService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    history.replaceState(null, '', '/');
    TestBed.resetTestingModule();
  });

  it('round-trips a canonical key without a watch path or URL normalization', () => {
    history.replaceState(null, '', '/studio?view=git#/tasks/AGT-2124');

    selection.restoreFromUrl();

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124'));
    expect(request.request.params.has('watchPath')).toBe(false);
    request.flush(detail);

    expect(selection.selected()?.info.key).toBe('AGT-2124');
    expect(`${location.pathname}${location.search}${location.hash}`)
      .toBe('/studio?view=git#/tasks/AGT-2124');
  });

  it('accepts a legacy locator once and replaces it with the stable key', () => {
    history.replaceState(
      null,
      '',
      '/?job=human-readable-slug&watchPath=C%3A%5Cprivate%5Cproject&view=git#diff',
    );

    selection.restoreFromUrl();

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'));
    expect(request.request.params.get('watchPath')).toBe('C:\\private\\project');
    request.flush(detail);

    expect(`${location.pathname}${location.search}${location.hash}`)
      .toBe('/?view=git#/tasks/AGT-2124&diff');
    expect(location.href).not.toContain('watchPath');
  });

  it('uses pushState for user navigation and clears selection on browser Back', () => {
    const push = vi.spyOn(history, 'pushState');

    selection.openDetail(info);

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'));
    expect(request.request.params.has('watchPath')).toBe(false);
    expect(request.request.params.get('project')).toBe('Agent Studio');
    request.flush(detail);
    expect(push).toHaveBeenCalled();
    expect(location.hash).toBe('#/tasks/AGT-2124');

    history.replaceState(null, '', '/?view=board');
    window.dispatchEvent(new PopStateEvent('popstate'));

    expect(selection.selected()).toBeNull();
    expect(selection.browserRouteCleared()).toBe(1);
    expect(location.search).toBe('?view=board');
  });

  it('rehydrates a search tab through live task identity instead of its stale lane path', () => {
    const staleTaskKey = 'C:\\private\\project\\5e-escalated\\human-readable-slug::human-readable-slug';
    const staleInfo = { ...info, taskKey: staleTaskKey };
    tasks.jobs.set([staleInfo]);

    selection.openDetailByTaskKey(staleTaskKey);

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'));
    expect(request.request.params.get('project')).toBe('Agent Studio');
    expect(request.request.params.has('watchPath')).toBe(false);
    request.flush({ info: staleInfo } as TaskDetail);

    expect(selection.detailLoading()).toBe(false);
    expect(selection.detailLoadError()).toBeNull();
    expect(selection.selected()?.info.id).toBe('human-readable-slug');
  });

  it('resolves a cold stale-lane tab through its containing registry project', () => {
    projects.setWorkspaces([{
      projects: [{
        id: 'PROJ-001',
        shortCode: 'AS',
        displayName: 'Agent Studio',
        storageLocation: 'C:\\private\\project',
      }],
    }] as unknown as RegistryWorkspaceListItem[]);
    const staleTaskKey = 'C:\\private\\project\\5e-escalated\\human-readable-slug::human-readable-slug';

    selection.openDetailByTaskKey(staleTaskKey);

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'));
    expect(request.request.params.get('project')).toBe('PROJ-001');
    expect(request.request.params.has('watchPath')).toBe(false);
    request.flush(detail);
  });

  it('ends a failed tab load with a retryable error state', () => {
    tasks.jobs.set([info]);

    selection.openDetailByTaskKey(info.taskKey);
    http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'))
      .flush({ title: 'Temporary failure' }, { status: 503, statusText: 'Unavailable' });

    expect(selection.detailLoading()).toBe(false);
    expect(selection.detailLoadError()?.taskLabel).toBe('AGT-2124');

    selection.retryDetailLoad();
    const retry = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'));
    retry.flush(detail);

    expect(selection.detailLoadError()).toBeNull();
    expect(selection.selected()).toEqual(detail);
  });
});

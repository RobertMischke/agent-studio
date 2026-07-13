import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import type { TaskDetail, TaskInfo } from '../../../models/task.model';
import { TaskSelectionService } from './task-selection.service';

describe('TaskSelectionService · stable task URLs', () => {
  let selection: TaskSelectionService;
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
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    history.replaceState(null, '', '/');
    TestBed.resetTestingModule();
  });

  it('round-trips a canonical key without a watch path or URL normalization', () => {
    history.replaceState(null, '', '/studio?task=AGT-2124&view=git#diff');

    selection.restoreFromUrl();

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124'));
    expect(request.request.params.has('watchPath')).toBe(false);
    request.flush(detail);

    expect(selection.selected()?.info.key).toBe('AGT-2124');
    expect(`${location.pathname}${location.search}${location.hash}`)
      .toBe('/studio?task=AGT-2124&view=git#diff');
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
      .toBe('/?view=git&task=AGT-2124#diff');
    expect(location.href).not.toContain('watchPath');
  });

  it('uses pushState for user navigation and clears selection on browser Back', () => {
    const push = vi.spyOn(history, 'pushState');

    selection.openDetail(info);

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124'));
    expect(request.request.params.has('watchPath')).toBe(false);
    request.flush(detail);
    expect(push).toHaveBeenCalled();
    expect(location.search).toBe('?task=AGT-2124');

    history.replaceState(null, '', '/?view=board');
    window.dispatchEvent(new PopStateEvent('popstate'));

    expect(selection.selected()).toBeNull();
    expect(selection.browserRouteCleared()).toBe(1);
    expect(location.search).toBe('?view=board');
  });
});

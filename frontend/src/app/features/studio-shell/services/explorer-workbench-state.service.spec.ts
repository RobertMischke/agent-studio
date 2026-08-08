import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { ExplorerWorkbenchStateService } from './explorer-workbench-state.service';

describe('ExplorerWorkbenchStateService', () => {
  beforeEach(() => {
    window.localStorage.clear();
    TestBed.resetTestingModule();
  });

  it('persists expanded state independently per project', () => {
    const state = TestBed.inject(ExplorerWorkbenchStateService);

    state.setExpanded('Alpha', true);
    state.setExpanded('Beta', true);
    state.setExpanded('Alpha', false);

    expect(state.isExpanded('Alpha')).toBe(false);
    expect(state.isExpanded('Beta')).toBe(true);
    expect(window.localStorage.getItem('atp.studio.explorer.workbenchSections'))
      .toBe('["Beta"]');

    TestBed.resetTestingModule();
    const restored = TestBed.inject(ExplorerWorkbenchStateService);
    expect(restored.isExpanded('Alpha')).toBe(false);
    expect(restored.isExpanded('Beta')).toBe(true);
  });
});

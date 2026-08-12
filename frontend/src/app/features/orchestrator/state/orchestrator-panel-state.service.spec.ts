import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { OrchestratorPanelStateService } from './orchestrator-panel-state.service';

describe('OrchestratorPanelStateService', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    TestBed.resetTestingModule();
  });

  it('restores the open posture and width from browser storage', () => {
    const first = TestBed.inject(OrchestratorPanelStateService);
    expect(first.open()).toBe(false);

    first.setOpen(true);
    first.setWidth(720);
    TestBed.resetTestingModule();

    const restored = TestBed.inject(OrchestratorPanelStateService);
    expect(restored.open()).toBe(true);
    expect(restored.width()).toBe(720);
  });

  it('persists an explicit close for the rest of the browser session', () => {
    sessionStorage.setItem('atp.studio.orchestratorOpen.v1', '1');
    const first = TestBed.inject(OrchestratorPanelStateService);
    first.setOpen(false);
    TestBed.resetTestingModule();

    expect(TestBed.inject(OrchestratorPanelStateService).open()).toBe(false);
  });
});

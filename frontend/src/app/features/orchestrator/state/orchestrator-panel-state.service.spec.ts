import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { OrchestratorPanelStateService } from './orchestrator-panel-state.service';

describe('OrchestratorPanelStateService', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    TestBed.configureTestingModule({});
  });

  it('defaults closed until the operator or explicit empty project entry opens it', () => {
    const state = TestBed.inject(OrchestratorPanelStateService);

    expect(state.open()).toBe(false);
    expect(state.hasPersistedOpenState()).toBe(false);
  });

  it('persists open and closed posture for the browser session', () => {
    const state = TestBed.inject(OrchestratorPanelStateService);
    state.setOpen(true);
    expect(sessionStorage.getItem('atp.studio.orchestratorOpen.v1')).toBe('1');

    state.setOpen(false);
    expect(state.open()).toBe(false);
    expect(state.hasPersistedOpenState()).toBe(true);
    expect(sessionStorage.getItem('atp.studio.orchestratorOpen.v1')).toBe('0');
  });

  it('restores open posture and panel width in a fresh injector', () => {
    sessionStorage.setItem('atp.studio.orchestratorOpen.v1', '1');
    localStorage.setItem('atp.studio.orchestratorWidth', '520');

    const state = TestBed.inject(OrchestratorPanelStateService);

    expect(state.open()).toBe(true);
    expect(state.hasPersistedOpenState()).toBe(true);
    expect(state.width()).toBe(520);
  });
});

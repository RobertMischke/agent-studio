import { TestBed } from '@angular/core/testing';
import { beforeEach } from 'vitest';
import { ExplorerWorkbenchStateService } from './explorer-workbench-state.service';

const STORAGE_KEY = 'atp.studio.explorer.workbenches.state.v2';

describe('ExplorerWorkbenchStateService', () => {
  beforeEach(() => {
    sessionStorage.removeItem(STORAGE_KEY);
    TestBed.resetTestingModule();
  });

  it('persists Dossier and status-group disclosures for the browser session', () => {
    const service = TestBed.inject(ExplorerWorkbenchStateService);

    service.setDossiersExpanded('Demo', true);
    service.setGroupExpanded('Demo', 'history', true);

    const persisted = JSON.parse(sessionStorage.getItem(STORAGE_KEY) ?? '{}');
    expect(persisted['Demo']).toEqual({
      dossiersExpanded: true,
      groups: {
        'needs-decision': true,
        'in-implementation': true,
        history: true,
      },
    });
  });

  it('collapses every Dossier branch for every known project', () => {
    const service = TestBed.inject(ExplorerWorkbenchStateService);
    service.setDossiersExpanded('Demo', true);
    service.setGroupExpanded('Demo', 'history', true);
    service.setDossiersExpanded('Other', true);

    service.collapseAll(['Demo', 'Other']);

    for (const projectName of ['Demo', 'Other']) {
      expect(service.stateFor(projectName)).toEqual({
        dossiersExpanded: false,
        groups: {
          'needs-decision': false,
          'in-implementation': false,
          history: false,
        },
      });
    }
  });
});

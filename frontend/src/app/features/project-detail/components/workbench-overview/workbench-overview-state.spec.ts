import type { WorkbenchOverviewItem, WorkbenchStatus } from '../../../../models/project-docs.model';
import {
  projectWorkbenchOverviewItems,
  readWorkbenchOverviewRouteState,
  writeWorkbenchOverviewRouteState,
  type WorkbenchOverviewSortKey,
  type WorkbenchOverviewViewOptions,
} from './workbench-overview-state';

function item(
  id: string,
  projectName: string,
  key: string,
  status: WorkbenchStatus,
  updatedAtUtc: string,
  openDecisionCount = 0,
  valid = true,
): WorkbenchOverviewItem {
  return {
    projectName,
    workbench: {
      id,
      key,
      title: `${id} title`,
      summary: `Hidden ${id} summary`,
      status,
      phase: status === 'active' ? 'testing' : null,
      updatedAtUtc,
      entryPath: `docs/${id}/index.html`,
      valid,
      error: null,
      sourceTaskKeys: [],
      openDecisionCount,
    },
  };
}

function project(
  items: readonly WorkbenchOverviewItem[],
  sortKey: WorkbenchOverviewSortKey,
  direction: WorkbenchOverviewViewOptions['direction'] = 'asc',
  query = '',
): string[] {
  return projectWorkbenchOverviewItems(items, { query, sortKey, direction })
    .map(entry => entry.workbench.id);
}

describe('workbench overview state', () => {
  const items = [
    item('alpha', 'Zulu', 'WB-20', 'active', '2026-08-10T10:00:00Z'),
    item('bravo', 'Alpha', 'WB-3', 'decided', '2026-08-12T10:00:00Z'),
    item('gamma', 'Alpha', 'WB-11', 'decision-pending', '2026-08-11T10:00:00Z', 4),
    item('delta', 'Beta', 'WB-2', 'invalid', '2026-08-09T10:00:00Z', 0, false),
  ];

  it('keeps the server projection untouched for the default order', () => {
    expect(project(items, 'default')).toEqual(['alpha', 'bravo', 'gamma', 'delta']);
  });

  it.each([
    ['status', 'asc', ['gamma', 'delta', 'alpha', 'bravo']],
    ['updated', 'desc', ['bravo', 'gamma', 'alpha', 'delta']],
    ['project', 'asc', ['bravo', 'gamma', 'delta', 'alpha']],
    ['key', 'asc', ['delta', 'bravo', 'gamma', 'alpha']],
    ['decisions', 'desc', ['gamma', 'alpha', 'bravo', 'delta']],
  ] as const)('sorts by %s in %s direction', (sortKey, direction, expected) => {
    expect(project(items, sortKey, direction)).toEqual(expected);
  });

  it('filters only across the visible key, title, project, and status fields', () => {
    expect(project(items, 'default', 'asc', 'wb-11')).toEqual(['gamma']);
    expect(project(items, 'default', 'asc', 'alpha')).toEqual(['alpha', 'bravo', 'gamma']);
    expect(project(items, 'default', 'asc', 'tracking')).toEqual(['bravo']);
    expect(project(items, 'default', 'asc', 'hidden gamma summary')).toEqual([]);
  });

  it('round-trips route-local state without disturbing sibling hash segments', () => {
    const hash = writeWorkbenchOverviewRouteState(
      '#/projects/proj-002/workbenches&filters=type%3Abug',
      { query: 'Review queue', sortKey: 'updated', direction: 'desc' },
    );
    expect(hash).toBe(
      '#/projects/proj-002/workbenches?view=q%3DReview%2Bqueue%26sort%3Dupdated%26dir%3Ddesc&filters=type%3Abug',
    );
    expect(readWorkbenchOverviewRouteState(hash)).toEqual({
      query: 'Review queue',
      sortKey: 'updated',
      direction: 'desc',
    });
  });

  it('does not attach overview state to an individual Dossier route', () => {
    const hash = '#/projects/proj-002/workbenches/route-lab&filters=x';
    expect(writeWorkbenchOverviewRouteState(hash, {
      query: 'route',
      sortKey: 'key',
      direction: 'asc',
    })).toBe(hash);
    expect(readWorkbenchOverviewRouteState(hash)).toBeNull();
  });
});

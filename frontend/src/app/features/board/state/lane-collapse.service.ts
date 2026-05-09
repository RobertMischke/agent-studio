import { Injectable, signal } from '@angular/core';

/**
 * Cycle 9 board feature service: lane and container collapse state for
 * the kanban shell. Lifted out of `app.ts` per ADR-0034 so the shell
 * stays a thin coordinator and the collapse state machine has a single
 * grep target. The state is persisted to localStorage under three keys
 * the shell already used:
 *
 *   - `collapsedLanes`                              - per-lane collapse Set
 *   - `atp.kanban.containers.collapsed`             - per-container collapse Set
 *   - `atp.kanban.containers.focused`               - container id under focus
 *   - `atp.kanban.containers.prefocus`              - pre-focus collapse snapshot
 *
 * Behaviour mirrors the pre-extraction app.ts methods one-to-one so
 * existing call sites only need to change `this.foo()` to
 * `this.laneCollapse.foo()`. The two methods that previously read
 * `this.laneGroups()` from the shell (`toggleContainerFocus`,
 * `containerSummary`) now take that data as a parameter instead - the
 * service stays pure state, the shell stays the source of truth for
 * the lane catalogue.
 */
@Injectable({ providedIn: 'root' })
export class LaneCollapseService {
  readonly collapsedLanes = signal<Set<string>>(
    new Set(safeParseStringArray(localStorage.getItem('collapsedLanes')))
  );

  readonly collapsedContainers = signal<Set<string>>(
    new Set(safeParseStringArray(localStorage.getItem('atp.kanban.containers.collapsed')))
  );

  readonly focusedContainer = signal<string | null>(
    localStorage.getItem('atp.kanban.containers.focused') || null
  );

  /**
   * Snapshot of the collapse Set captured the moment the user focus-
   * expanded a container, so a second click on the same focus button
   * restores whatever state the user had before. `null` when no focus
   * is active.
   */
  private prefocusCollapsedContainers: string[] | null = (() => {
    const raw = localStorage.getItem('atp.kanban.containers.prefocus');
    if (!raw) return null;
    try {
      const parsed = JSON.parse(raw);
      return Array.isArray(parsed) ? parsed.filter((s): s is string => typeof s === 'string') : null;
    } catch {
      return null;
    }
  })();

  // ---------- per-lane collapse ----------

  toggleLaneCollapse(state: string): void {
    const current = new Set(this.collapsedLanes());
    if (current.has(state)) current.delete(state);
    else current.add(state);
    this.collapsedLanes.set(current);
    localStorage.setItem('collapsedLanes', JSON.stringify([...current]));
  }

  isLaneCollapsed(state: string): boolean {
    return this.collapsedLanes().has(state);
  }

  /**
   * Flex-grow factor for a lane group. The dashboard distributes its
   * leftover horizontal space across the three groups; if every group
   * grew by the same factor (`flex: 1 1 auto`) groups with fewer
   * expanded lanes ended up with wider lanes than groups with more
   * expanded lanes (e.g. Ready in a 3-lane Backlog became visibly
   * wider than In Progress in a 2-lane Active stretch). Growing each
   * group in proportion to its expanded-lane count makes every
   * expanded lane settle at the same rendered width regardless of
   * which group it lives in. Zero is intentional when every lane in
   * the group is collapsed: the group then sizes to its rails only.
   */
  expandedLaneCount(group: { lanes: Array<{ state: string }> }): number {
    return group.lanes.reduce((n, l) => n + (this.isLaneCollapsed(l.state) ? 0 : 1), 0);
  }

  // ---------- per-container collapse + focus ----------

  isContainerCollapsed(id: string): boolean {
    return this.collapsedContainers().has(id);
  }

  toggleContainerCollapse(id: string): void {
    // A direct toggle drops focus mode if it was active for this id
    // (the user is taking manual control), but leaves focus alone if
    // the toggle targets a container that was already collapsed by
    // focus mode - they're just toggling a non-focused container.
    if (this.focusedContainer() === id) {
      this.clearFocus();
    }
    const current = new Set(this.collapsedContainers());
    if (current.has(id)) current.delete(id);
    else current.add(id);
    this.collapsedContainers.set(current);
    this.persistContainerState();
  }

  isContainerFocused(id: string): boolean {
    return this.focusedContainer() === id;
  }

  /**
   * Focus-expand a container by collapsing the others to summary strips.
   * `allContainerIds` is the full id list from the shell's `laneGroups`
   * (the service stays decoupled from how lanes are grouped).
   */
  toggleContainerFocus(id: string, allContainerIds: string[]): void {
    if (this.focusedContainer() === id) {
      // Second click on the same focus button: restore the pre-focus
      // snapshot so a tap is reversible.
      const restored = new Set(this.prefocusCollapsedContainers ?? []);
      this.collapsedContainers.set(restored);
      this.focusedContainer.set(null);
      this.prefocusCollapsedContainers = null;
      localStorage.removeItem('atp.kanban.containers.focused');
      localStorage.removeItem('atp.kanban.containers.prefocus');
      this.persistContainerState();
      return;
    }
    // Snapshot the current collapse set so the toggle is reversible.
    if (this.focusedContainer() === null) {
      this.prefocusCollapsedContainers = [...this.collapsedContainers()];
      localStorage.setItem(
        'atp.kanban.containers.prefocus',
        JSON.stringify(this.prefocusCollapsedContainers)
      );
    }
    const next = new Set<string>();
    for (const other of allContainerIds) {
      if (other !== id) next.add(other);
    }
    this.collapsedContainers.set(next);
    this.focusedContainer.set(id);
    localStorage.setItem('atp.kanban.containers.focused', id);
    this.persistContainerState();
  }

  resetContainers(): void {
    this.collapsedContainers.set(new Set());
    this.focusedContainer.set(null);
    this.prefocusCollapsedContainers = null;
    localStorage.removeItem('atp.kanban.containers.focused');
    localStorage.removeItem('atp.kanban.containers.prefocus');
    this.persistContainerState();
  }

  /**
   * Per-container summary chips shown when the container is collapsed.
   * One chip per lane: `<icon>x<count>`. Empty lanes are kept so the
   * shape of the container stays readable at a glance.
   *
   * Takes the lane group as a parameter because the lane catalogue is
   * derived in the shell (from the JobService grouped feed); the
   * service stays free of UI / job-shape coupling.
   */
  containerSummary(
    group: { lanes: ReadonlyArray<{ state: string; icon: string; title: string; jobs: ReadonlyArray<unknown> }> } | null | undefined
  ): Array<{ state: string; icon: string; title: string; count: number }> {
    if (!group) return [];
    return group.lanes.map(l => ({
      state: l.state,
      icon: l.icon,
      title: l.title,
      count: l.jobs.length
    }));
  }

  private clearFocus(): void {
    if (this.focusedContainer() !== null) {
      this.focusedContainer.set(null);
      this.prefocusCollapsedContainers = null;
      localStorage.removeItem('atp.kanban.containers.focused');
      localStorage.removeItem('atp.kanban.containers.prefocus');
    }
  }

  private persistContainerState(): void {
    localStorage.setItem(
      'atp.kanban.containers.collapsed',
      JSON.stringify([...this.collapsedContainers()])
    );
  }
}

function safeParseStringArray(raw: string | null): string[] {
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter((s): s is string => typeof s === 'string') : [];
  } catch {
    return [];
  }
}

import { Injectable, computed, inject, signal } from '@angular/core';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import type { TaskInfo, WatchPathEntry } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';

/**
 * Owns the explorer sidebar's "drag a project onto another project to
 * reassign it to that workspace" flow. Lifted out of StudioShellComponent
 * so the shell stays within the component-size budget and so the
 * stateful pieces (dragging name, hovered drop target, in-flight move,
 * error message) are addressable from a Playwright spec without going
 * through the entire shell.
 *
 * Project name and watch-path name are 1:1 in this app — the backend
 * stamps `TaskInfo.projectName` from the matching `WatchPathEntry.Name` —
 * so each project row in the explorer is effectively both the project
 * entry AND its workspace header. Dropping project A onto project B's
 * row means: move every job in project A to project B's watch path.
 */
@Injectable({ providedIn: 'root' })
export class ProjectDragDropService {
  private readonly jobService = inject(TaskService);

  readonly draggingProjectName = signal<string | null>(null);
  readonly dragOverProjectName = signal<string | null>(null);
  readonly movingProjectName = signal<string | null>(null);
  readonly moveErrorMessage = signal<string | null>(null);

  private workspaces = signal<readonly WatchPathEntry[]>([]);

  /** The host (StudioShellComponent) calls this whenever its
   *  `workspaces` input changes; lets the service resolve project
   *  names to watch-path values without needing a second source. */
  setWorkspaces(entries: readonly WatchPathEntry[]): void {
    this.workspaces.set(entries);
  }

  private workspaceFor(projectName: string): WatchPathEntry | null {
    return this.workspaces().find(w => w.name === projectName) ?? null;
  }

  /** True only when both endpoints map to a real watch path and the
   *  user is not dropping onto the source row. */
  canDropProjectOn(source: string | null, target: string): boolean {
    if (!source || source === target) return false;
    return this.workspaceFor(source) !== null && this.workspaceFor(target) !== null;
  }

  onDragStart(event: DragEvent, projectName: string): void {
    if (!event.dataTransfer) return;
    if (this.workspaceFor(projectName) === null) {
      event.preventDefault();
      return;
    }
    event.dataTransfer.effectAllowed = 'move';
    try { event.dataTransfer.setData('text/x-studio-project', projectName); } catch { /* ignore */ }
    this.draggingProjectName.set(projectName);
    this.moveErrorMessage.set(null);
  }

  onDragOver(event: DragEvent, overName: string): void {
    const source = this.draggingProjectName();
    if (!source) return;
    if (!this.canDropProjectOn(source, overName)) {
      if (event.dataTransfer) event.dataTransfer.dropEffect = 'none';
      return;
    }
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
    if (this.dragOverProjectName() !== overName) {
      this.dragOverProjectName.set(overName);
    }
  }

  onDragLeave(overName: string): void {
    if (this.dragOverProjectName() === overName) {
      this.dragOverProjectName.set(null);
    }
  }

  onDrop(event: DragEvent, overName: string, jobs: readonly TaskInfo[]): void {
    event.preventDefault();
    const source = this.draggingProjectName();
    this.draggingProjectName.set(null);
    this.dragOverProjectName.set(null);
    if (!source) return;
    if (!this.canDropProjectOn(source, overName)) return;
    this.moveProjectToWorkspace(source, overName, jobs.filter(j => j.projectName === source));
  }

  onDragEnd(): void {
    this.draggingProjectName.set(null);
    this.dragOverProjectName.set(null);
  }

  /** Fan out one change-project call per job in `sourceJobs`. The
   *  shell refreshes the job list when the fan-out settles so the
   *  source row shows its new (zero) count and the target's count
   *  grows. Mirrors the per-project Settings dropdown in
   *  ProjectWorkspaceSectionComponent. */
  private moveProjectToWorkspace(
    sourceProject: string,
    targetProject: string,
    sourceJobs: readonly TaskInfo[],
  ): void {
    const target = this.workspaceFor(targetProject);
    if (!target) return;
    if (sourceJobs.length === 0) {
      this.moveErrorMessage.set(`Project "${sourceProject}" has no jobs to move.`);
      this.jobService.refresh(true);
      return;
    }
    this.movingProjectName.set(sourceProject);
    this.moveErrorMessage.set(null);
    const calls = sourceJobs.map(j =>
      this.jobService.changeProject(j.id, target.path, j.watchPath).pipe(
        map(() => ({ ok: true as const })),
        catchError(() => of({ ok: false as const })),
      ),
    );
    forkJoin(calls).subscribe({
      next: (results) => {
        this.movingProjectName.set(null);
        const failures = results.filter(r => !r.ok).length;
        if (failures > 0) {
          this.moveErrorMessage.set(
            `Moved ${results.length - failures} of ${results.length} jobs from "${sourceProject}" to "${targetProject}"; ${failures} failed.`,
          );
        }
        this.jobService.refresh(true);
      },
      error: () => {
        this.movingProjectName.set(null);
        this.moveErrorMessage.set(`Could not move "${sourceProject}" to "${targetProject}".`);
      },
    });
  }

  /** Test helper: clear all drag state and any lingering error. */
  reset(): void {
    this.draggingProjectName.set(null);
    this.dragOverProjectName.set(null);
    this.movingProjectName.set(null);
    this.moveErrorMessage.set(null);
  }

  /** Convenience computed: is any move currently in flight? */
  readonly isMoving = computed(() => this.movingProjectName() !== null);
}

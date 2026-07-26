import { Injectable, computed, signal } from '@angular/core';

/**
 * Owns the explorer sidebar's "drag a project onto a workspace folder to
 * reassign its workspace membership" flow (F46 two-level tree). Lifted out
 * of StudioShellComponent so the shell stays within its component-size
 * budget and so the stateful pieces (dragged project, hovered workspace,
 * in-flight move, error message) are addressable from a Playwright spec
 * without going through the whole shell.
 *
 * Pre-F46 the tree was a single flat project list and the gesture "drop
 * project A onto project B's row" merged A's jobs into B's watch path. After
 * F46 the tree is workspace -> project, so the gesture now targets a
 * WORKSPACE folder: dropping project P onto workspace W reassigns P's
 * registry `workspaceId` to W via PUT /api/projects/{id}. No job folder is
 * moved on disk; the registry is the source of truth for the grouping, so
 * reloading it re-homes the row under its new workspace (ADR-0042 /
 * ADR-0048). The shell owns the PUT + reload; this service only owns the
 * drag lifecycle state and the drop-validity rule.
 */
@Injectable({ providedIn: 'root' })
export class ProjectDragDropService {
  /** Registry id (PROJ-NNN) of the project row being dragged, or null. */
  readonly draggingProjectId = signal<string | null>(null);
  /** Job-derived name of the dragged row (drives the source-row fade + tooltip). */
  readonly draggingProjectName = signal<string | null>(null);
  /** Workspace the dragged project currently lives in; rejects same-workspace drops. */
  readonly draggingSourceWorkspaceId = signal<string | null>(null);
  /** Workspace folder currently hovered as a candidate drop target. */
  readonly dragOverWorkspaceId = signal<string | null>(null);
  /** Project id whose reassignment is in flight (drives the source-row pulse). */
  readonly movingProjectId = signal<string | null>(null);
  readonly moveErrorMessage = signal<string | null>(null);

  /** A real registry workspace the move can persist to — not one of the
   *  synthetic tree buckets ("__all__" empty-registry fallback,
   *  "__unassigned__" no-registry-match group). */
  private isRealWorkspace(id: string): boolean {
    return id !== '__all__' && id !== '__unassigned__';
  }

  /** True only when a registered project is being dragged and the target is a
   *  real workspace that differs from the project's current one. */
  canDropOnWorkspace(targetWorkspaceId: string): boolean {
    if (!this.draggingProjectId()) return false;
    if (!this.isRealWorkspace(targetWorkspaceId)) return false;
    return this.draggingSourceWorkspaceId() !== targetWorkspaceId;
  }

  canMoveProjectToWorkspace(
    project: { projectId: string | null; workspaceId: string | null },
    targetWorkspaceId: string,
  ): boolean {
    return !!project.projectId
      && this.isRealWorkspace(targetWorkspaceId)
      && project.workspaceId !== targetWorkspaceId;
  }

  onDragStart(
    project: { projectId: string | null; name: string; workspaceId: string | null },
  ): void {
    // A row can appear in either synthetic bucket and still be registered.
    // Only rows without a registry id have no membership record to update.
    if (!project.projectId) return;
    this.draggingProjectId.set(project.projectId);
    this.draggingProjectName.set(project.name);
    this.draggingSourceWorkspaceId.set(project.workspaceId);
    this.moveErrorMessage.set(null);
  }

  onWorkspaceDragEnter(targetWorkspaceId: string): void {
    if (!this.canDropOnWorkspace(targetWorkspaceId)) return;
    if (this.dragOverWorkspaceId() !== targetWorkspaceId) {
      this.dragOverWorkspaceId.set(targetWorkspaceId);
    }
  }

  onWorkspaceDragLeave(targetWorkspaceId: string): void {
    if (this.dragOverWorkspaceId() === targetWorkspaceId) {
      this.dragOverWorkspaceId.set(null);
    }
  }

  onDragEnd(): void {
    this.draggingProjectId.set(null);
    this.draggingProjectName.set(null);
    this.draggingSourceWorkspaceId.set(null);
    this.dragOverWorkspaceId.set(null);
  }

  /** Test helper: clear all drag state and any lingering error. */
  reset(): void {
    this.onDragEnd();
    this.movingProjectId.set(null);
    this.moveErrorMessage.set(null);
  }

  /** Convenience computed: is a reassignment currently in flight? */
  readonly isMoving = computed(() => this.movingProjectId() !== null);
}

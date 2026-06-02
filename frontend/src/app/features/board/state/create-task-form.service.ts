import { Injectable, inject, signal } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { CliType, CLI_TYPES, TaskKind, WatchPathEntry } from '../../../models/task.model';
import type { CliModelInfo } from '../../../features/cli';
import type { PendingAttachment } from '../components/create-task-dialog/create-task-dialog.component';
import { TaskService } from '../../../services/task.service';
import { CliCatalogStore } from '../../../services/cli-catalog.store';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import { CLIENT_ID } from '../../../services/client-id.interceptor';

/**
 * Cycle 10a board-feature service: owns every field the create-job
 * dialog binds, plus the four open-prefilled entry points the shell
 * used to host inline (default open, security-follow-up,
 * uxui-follow-up, orchestrator-draft-follow-up).
 *
 * Lifted out of `app.ts` per ADR-0034. The shell now just renders
 * `<app-create-job-dialog>` against this service's bound fields and
 * delegates open / cancel / submit to it. The shell still passes
 * `watchPaths` + `activeProjects` into the open methods because those
 * live on the shell (workspace-scoped); the service stays free of
 * cross-feature coupling.
 *
 * Refresh-after-submit is exposed as the `submitted$` event the shell
 * subscribes to; the service does not call `TaskService.refresh` itself
 * because the shell owns that orchestration concern.
 */
@Injectable({ providedIn: 'root' })
export class CreateTaskFormService {
  private readonly jobService = inject(TaskService);
  private readonly catalogStore = inject(CliCatalogStore);
  private readonly errorDialog = inject(ErrorDialogService);

  readonly visible = signal(false);

  // Plain mutable fields — these are [(ngModel)]-bound by the dialog and
  // mutated directly. Keeping them as fields (not signals) preserves the
  // previous template ergonomics.
  newTitle = '';
  newWatchPath = '';
  newAgent: CliType = 'copilot';
  newPrompt = '';
  newTargetState = '1-preparation';
  newTaskType = 'chore';
  newTags: string[] = [];
  /** Card kind: `task` (default) or `epic`. */
  newKind: TaskKind = 'task';
  /** Assignment way 1: optional parent epic id for a `kind=task` create. */
  newEpicId = '';
  newCliType: CliType = readDefaultCliPref();
  newModel: string = readDefaultModelPref(readDefaultCliPref());
  newAttachments: PendingAttachment[] = [];

  /** Allowed manual-create lanes (everything before 3-progress). */
  static readonly ALLOWED_TARGET_STATES = ['0-backlog', '1-preparation', '2-ready'] as const;

  readonly availableModels = signal<CliModelInfo[]>([]);

  /** Fired after a successful createJob; shell listens to refresh the board. */
  private readonly submittedSubject = new Subject<{ jobId: string }>();
  readonly submitted$: Observable<{ jobId: string }> = this.submittedSubject.asObservable();

  /**
   * Whether a "+ Add task" button is allowed in the named lane. Lanes
   * past 2-ready don't accept manual creation — they fill via
   * orchestrator runs / triage.
   */
  canAddTaskToGroup(state: string): boolean {
    return state === '0-backlog' || state === '1-preparation' || state === '2-ready';
  }

  // ---------- open entry points ----------

  /**
   * Default open from the toolbar `+ New` button or a lane "Add task"
   * affordance. Picks the watch path from the user's last-used + active
   * projects. `targetState` is honoured when it names one of the manual
   * create lanes (`0-backlog` / `1-preparation` / `2-ready`); anything
   * else falls back to `1-preparation`.
   */
  open(opts: {
    watchPaths: readonly WatchPathEntry[];
    activeProjects: ReadonlySet<string>;
    targetState?: string;
  }): void {
    const requested = opts.targetState ?? '';
    this.newTargetState = (CreateTaskFormService.ALLOWED_TARGET_STATES as readonly string[]).includes(requested)
      ? requested
      : '1-preparation';
    this.newWatchPath = pickCreateWatchPath(opts.watchPaths, opts.activeProjects);
    this.loadCreateModels(this.newCliType);
    this.visible.set(true);
  }

  /**
   * Project-Security panel "Create follow-up" action. Pre-fills the
   * dialog with a prompt body the panel composed from a recent review.
   */
  openSecurityFollowUp(
    event: { projectName: string; prefill: string },
    watchPaths: readonly WatchPathEntry[],
  ): void {
    const watchEntry = watchPaths.find((wp) => wp.name === event.projectName);
    if (!watchEntry) return;
    this.newTargetState = '1-preparation';
    this.newWatchPath = watchEntry.path;
    this.newPrompt = event.prefill;
    this.newTitle = `Security follow-up (${event.projectName})`;
    this.loadCreateModels(this.newCliType);
    this.visible.set(true);
  }

  /**
   * Project-UXUI panel "Create follow-up" / per-row "Task" action.
   * Pre-fills the dialog with a prompt body composed from a council
   * note or design overview, plus a panel-supplied title.
   */
  openUxuiFollowUp(
    event: { projectName: string; prefill: string; title: string },
    watchPaths: readonly WatchPathEntry[],
  ): void {
    const watchEntry = watchPaths.find((wp) => wp.name === event.projectName);
    if (!watchEntry) return;
    this.newTargetState = '1-preparation';
    this.newWatchPath = watchEntry.path;
    this.newPrompt = event.prefill;
    this.newTitle = event.title;
    this.loadCreateModels(this.newCliType);
    this.visible.set(true);
  }

  /**
   * Orchestrator-draft "Create task from this draft" action. Title is
   * derived from the prompt's first non-empty line.
   */
  openOrchestratorDraftFollowUp(
    event: { projectName: string; promptText: string },
    watchPaths: readonly WatchPathEntry[],
  ): void {
    const watchEntry = watchPaths.find((wp) => wp.name === event.projectName);
    if (!watchEntry) return;
    this.newTargetState = '1-preparation';
    this.newWatchPath = watchEntry.path;
    this.newPrompt = event.promptText;
    this.newTitle = deriveDraftTitle(event.promptText);
    this.loadCreateModels(this.newCliType);
    this.visible.set(true);
  }

  // ---------- ngModel-driven mutators ----------

  onCreateCliTypeChange(t: CliType): void {
    if (this.newCliType === t) return;
    this.newCliType = t;
    this.newModel = readDefaultModelPref(t);
    this.loadCreateModels(t);
  }

  /** Mirrors a global "default model for CLI X" change into the form when relevant. */
  onDefaultModelChange(ev: { cliType: CliType; model: string }): void {
    if (ev.cliType === this.newCliType) {
      this.newModel = ev.model;
    }
  }

  /** Apply user's stored CLI default before showing the dialog. */
  applyStoredCliDefault(): void {
    const t = readDefaultCliPref();
    this.newCliType = t;
    this.newModel = readDefaultModelPref(t);
  }

  // ---------- close + submit ----------

  cancel(): void {
    this.visible.set(false);
    this.newTitle = '';
    this.newPrompt = '';
    this.newAgent = 'copilot';
    this.newTaskType = 'chore';
    this.newTags = [];
    this.newTargetState = '1-preparation';
    this.newKind = 'task';
    this.newEpicId = '';
    this.newCliType = readDefaultCliPref();
    this.newModel = readDefaultModelPref(this.newCliType);
    this.availableModels.set([]);
    for (const att of this.newAttachments) URL.revokeObjectURL(att.previewUrl);
    this.newAttachments = [];
  }

  /**
   * Persist the draft. On success: store the chosen watch path as
   * "last used", upload pending attachments (if any), close the dialog,
   * fire `submitted$` so the shell can refresh.
   */
  submit(): void {
    const attachments = this.newAttachments;
    const promptDraft = this.newPrompt.trim();
    const watchPath = this.newWatchPath;

    // When attachments are present we defer writing the prompt to the create
    // call (its `pending-attachment-…` placeholders are not yet resolvable),
    // upload each image against the new jobId, then PUT prompt.md with the
    // real `attachments/<file>` references.
    const initialPrompt = attachments.length > 0 ? undefined : (promptDraft || undefined);

    this.jobService.createJob({
      title: this.newTitle.trim(),
      watchPath,
      agent: this.newCliType,
      promptMarkdown: initialPrompt,
      targetState: this.newTargetState,
      cliType: this.newCliType,
      model: this.newModel.trim() || undefined,
      taskType: this.newTaskType,
      tags: this.newTags.length > 0 ? [...this.newTags] : undefined,
      kind: this.newKind,
      // Way 1 only applies to a task; an epic has no parent epic.
      epicId: this.newKind === 'task' && this.newEpicId ? this.newEpicId : undefined,
    }).subscribe({
      next: (res) => {
        localStorage.setItem('lastCreateWatchPath', watchPath);
        if (attachments.length > 0) {
          void this.uploadCreateAttachments(res.id, watchPath, promptDraft, attachments);
        }
        this.cancel();
        this.submittedSubject.next({ jobId: res.id });
      },
      error: (err) => {
        this.jobService.error.set(err.error || 'Failed to create job');
        this.errorDialog.show(err, {
          title: 'Failed to create task',
          fallbackMessage: 'Failed to create task',
          source: 'Task creation',
        });
      },
    });
  }

  // ---------- internal ----------

  private loadCreateModels(cliType: CliType): void {
    // ADR-0046: prefer the shared cache so the Create dialog renders the
    // model dropdown synchronously when the user opens it.
    if (this.catalogStore.hasFresh(cliType)) {
      const models = [...this.catalogStore.modelsFor(cliType)];
      this.availableModels.set(models);
      if (!this.newModel) {
        const def = models.find((m) => m.isDefault);
        if (def) this.newModel = def.id;
      }
      return;
    }
    this.catalogStore.ensure(cliType).subscribe({
      next: (models) => {
        const list = [...models];
        this.availableModels.set(list);
        if (!this.newModel) {
          const def = list.find((m) => m.isDefault);
          if (def) this.newModel = def.id;
        }
      },
      error: () => this.availableModels.set([]),
    });
  }

  private async uploadCreateAttachments(
    jobId: string,
    watchPath: string,
    promptDraft: string,
    attachments: PendingAttachment[],
  ): Promise<void> {
    let prompt = promptDraft;
    for (const att of attachments) {
      try {
        const form = new FormData();
        form.append('file', att.file, att.file.name || `${att.alt}.png`);
        const url = `/api/tasks/${encodeURIComponent(jobId)}/attachments`
          + (watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '');
        // The job folder was created milliseconds ago. Backend caches can
        // race against that creation under concurrent polling, returning a
        // transient 400/404 "Job not found" before the cache observes the
        // mutation. Retry once with a short backoff so a brief cache miss
        // never surfaces as a user-visible upload failure.
        let res = await fetch(url, { method: 'POST', body: form, headers: { 'X-Client-Id': CLIENT_ID } });
        if (!res.ok && (res.status === 400 || res.status === 404)) {
          await new Promise(r => setTimeout(r, 250));
          // FormData stream is consumed - rebuild it for the retry.
          const retry = new FormData();
          retry.append('file', att.file, att.file.name || `${att.alt}.png`);
          res = await fetch(url, { method: 'POST', body: retry, headers: { 'X-Client-Id': CLIENT_ID } });
        }
        if (!res.ok) {
          this.errorDialog.show(new Error(`Upload failed (${res.status}) for ${att.file.name || att.alt}`), {
            title: 'Attachment upload failed',
            fallbackMessage: 'Could not upload one of the pasted images.',
            source: `Task ${jobId}`,
          });
          continue;
        }
        const payload = (await res.json()) as { fileName: string; relativePath: string };
        prompt = prompt.replace(
          new RegExp(`pending-attachment-${escapeRegex(att.id)}`, 'g'),
          payload.relativePath,
        );
      } catch (e) {
        this.errorDialog.show(e as Error, {
          title: 'Attachment upload failed',
          fallbackMessage: 'Could not upload one of the pasted images.',
          source: `Task ${jobId}`,
        });
      } finally {
        URL.revokeObjectURL(att.previewUrl);
      }
    }
    this.jobService.updateJobFile(jobId, 'prompt.md', prompt, watchPath).subscribe({
      next: () => this.submittedSubject.next({ jobId }),
      error: (err) => this.errorDialog.show(err, {
        title: 'Failed to save prompt',
        fallbackMessage: 'Attachments uploaded, but writing prompt.md failed.',
        source: `Task ${jobId}`,
      }),
    });
  }
}

// ---------- module-private helpers ----------

function readDefaultCliPref(): CliType {
  const stored = localStorage.getItem('defaultCliType') as CliType | null;
  if (stored && (CLI_TYPES as string[]).includes(stored)) return stored;
  return 'claude';
}

function readDefaultModelPref(cliType: CliType): string {
  return localStorage.getItem('defaultModel:' + cliType) ?? '';
}

function deriveDraftTitle(text: string): string {
  if (!text) return '';
  for (const raw of text.split('\n')) {
    const line = raw.replace(/^#+\s*/, '').replace(/[*_`]/g, '').trim();
    if (line.length === 0) continue;
    return line.length > 80 ? line.slice(0, 77).trim() + '...' : line;
  }
  return '';
}

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function pickCreateWatchPath(
  paths: readonly WatchPathEntry[],
  active: ReadonlySet<string>,
): string {
  if (paths.length === 0) return '';
  const last = localStorage.getItem('lastCreateWatchPath');
  const isValid = (p: string | null) => !!p && paths.some((wp) => wp.path === p);
  const activePaths = paths.filter((wp) => active.has(wp.name));

  if (activePaths.length === 1) {
    return activePaths[0].path;
  }
  if (activePaths.length > 1) {
    const lastInActive = activePaths.find((wp) => wp.path === last);
    if (lastInActive) return lastInActive.path;
    return activePaths[0].path;
  }
  if (isValid(last)) return last as string;
  return paths[0].path;
}

import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { finalize, tap } from 'rxjs';
import type { ProjectUrlProcessSnapshot, RegistryProjectUrl } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';

/** Component-scoped owner for process polling and inline-console state. */
@Injectable()
export class ProjectUrlProcessController {
  private readonly tasks = inject(TaskService);
  private readonly pollMs = 750;
  private processTimer: ReturnType<typeof setTimeout> | null = null;
  private projectId: string | null = null;
  private urlId: string | null = null;

  readonly session = signal<ProjectUrlProcessSnapshot | null>(null);
  readonly consoleOpen = signal(false);
  readonly stopping = signal(false);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.clearTimer());
  }

  reset(): void {
    this.clearTimer();
    this.projectId = null;
    this.urlId = null;
    this.session.set(null);
    this.consoleOpen.set(false);
    this.stopping.set(false);
  }

  refresh(projectId: string, urlId: string): void {
    this.projectId = projectId;
    this.urlId = urlId;
    this.tasks.getProjectUrlProcess(projectId, urlId).subscribe({
      next: session => {
        if (this.projectId !== projectId || this.urlId !== urlId) return;
        this.session.set(session);
        if (session?.state === 'starting') this.consoleOpen.set(true);
        this.schedulePoll();
      },
      error: () => { /* An absent owned process is a normal empty state. */ },
    });
  }

  start(projectId: string, url: RegistryProjectUrl, effectiveCwd: string) {
    const rule = url.startRule;
    if (!rule) throw new Error('A start rule is required.');
    this.projectId = projectId;
    this.urlId = url.id;
    this.consoleOpen.set(true);
    this.session.set({
      started: false,
      projectId,
      urlId: url.id,
      command: rule.command,
      cwd: effectiveCwd,
      state: 'starting',
      processId: null,
      startedAtUtc: new Date().toISOString(),
      finishedAtUtc: null,
      exitCode: null,
      output: ['[studio] Starting the configured dev server…'],
    });
    this.schedulePoll();
    return this.tasks.startProjectUrl(projectId, url.id).pipe(tap(session => {
      this.session.set(session);
      this.schedulePoll();
    }));
  }

  failStart(explanation: string, command: string, cwd: string): void {
    this.session.update(session => session ? {
      ...session,
      command,
      cwd,
      state: 'failed',
      finishedAtUtc: new Date().toISOString(),
      output: [...session.output, `[studio] ${explanation}`],
    } : session);
  }

  stop(projectId: string, urlId: string) {
    this.stopping.set(true);
    return this.tasks.stopProjectUrlProcess(projectId, urlId).pipe(
      tap(session => this.session.set(session)),
      finalize(() => this.stopping.set(false)),
    );
  }

  appendError(message: string): void {
    this.session.update(session => session
      ? { ...session, output: [...session.output, `[studio] ${message}`] }
      : session);
  }

  private schedulePoll(): void {
    this.clearTimer();
    const state = this.session()?.state;
    if (!this.projectId || !this.urlId || (state !== 'starting' && state !== 'running')) return;
    const projectId = this.projectId;
    const urlId = this.urlId;
    this.processTimer = setTimeout(() => this.refresh(projectId, urlId), this.pollMs);
  }

  private clearTimer(): void {
    if (!this.processTimer) return;
    clearTimeout(this.processTimer);
    this.processTimer = null;
  }
}

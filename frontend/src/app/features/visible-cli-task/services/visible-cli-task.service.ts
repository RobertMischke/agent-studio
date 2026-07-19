import { Injectable, inject } from '@angular/core';
import { finalize, map } from 'rxjs';
import { TaskService } from '../../../services/task.service';
import type { VisibleCliTaskCreated, VisibleCliTaskRequest } from '../models/visible-cli-task.model';

/** Starts small, observable CLI actions on the existing durable task substrate. */
@Injectable({ providedIn: 'root' })
export class VisibleCliTaskService {
  private readonly tasks = inject(TaskService);

  start(request: VisibleCliTaskRequest, watchPath: string) {
    const startedAt = performance.now();
    console.info('[visible-cli-task] start', {
      event: 'visible-cli-task.start',
      title: request.title,
      command: request.command,
      watchPath,
    });
    return this.tasks.createJob({
      title: request.title,
      agent: request.cliType ?? 'codex',
      cliType: request.cliType ?? 'codex',
      watchPath,
      targetState: '2-ready',
      taskType: 'chore',
      promptMarkdown: buildVisibleCliTaskPrompt(request),
    }).pipe(
      map(({ id }): VisibleCliTaskCreated => ({ jobId: id, watchPath })),
      finalize(() => console.info('[visible-cli-task] settled', {
        event: 'visible-cli-task.settled',
        title: request.title,
        durationMs: Math.round(performance.now() - startedAt),
      })),
    );
  }
}

export function buildVisibleCliTaskPrompt(request: VisibleCliTaskRequest): string {
  const context = Object.entries(request.context ?? {})
    .map(([key, value]) => `- ${key}: ${value}`)
    .join('\n');
  return [
    `# ${request.scope}`,
    '',
    request.reason,
    '',
    '## CLI input',
    '',
    request.prompt,
    '',
    '## Execution contract',
    '',
    `- Command: \`${request.command}\``,
    `- Expected duration: ${request.expectedDuration ?? 'No historical estimate yet'}`,
    ...(context ? ['', '## Context', '', context] : []),
    '',
    'Keep all progress and output in this task conversation. Finish with a concise outcome and any required next action.',
  ].join('\n');
}

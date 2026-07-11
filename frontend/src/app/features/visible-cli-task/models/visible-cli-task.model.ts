import type { CliType, WatchPathEntry } from '../../../models/task.model';

/** Public contract for any product action that should run as a visible CLI task. */
export interface VisibleCliTaskRequest {
  title: string;
  scope: string;
  reason: string;
  command: string;
  prompt: string;
  expectedDuration?: string;
  context?: Readonly<Record<string, string>>;
  cliType?: CliType;
}

export interface VisibleCliTaskCreated {
  jobId: string;
  watchPath: string;
}

export type VisibleCliTaskWorkspace = Pick<WatchPathEntry, 'name' | 'path'>;

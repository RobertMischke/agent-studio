import { Injectable, signal } from '@angular/core';

export interface DiffContextSelection {
  projectName: string;
  commitSha: string;
  path: string | null;
  lineRanges?: { startLine: number; endLine: number }[];
}

/** Shares the active Git surface selection with the context-envelope composer. */
@Injectable({ providedIn: 'root' })
export class OrchestratorSurfaceContextService {
  readonly diffSelection = signal<DiffContextSelection | null>(null);

  selectDiff(
    projectName: string,
    commitSha: string,
    selection: Omit<DiffContextSelection, 'projectName' | 'commitSha'>,
  ): void {
    this.diffSelection.set({ projectName, commitSha, ...selection });
  }
}

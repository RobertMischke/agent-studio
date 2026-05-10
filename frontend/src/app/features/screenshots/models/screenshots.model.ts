/**
 * Cycle 9 screenshots feature models. Lifted out of
 * `models/job.model.ts` per ADR-0034. Re-exported from the legacy file.
 *
 * Per-job + workspace-wide screenshot listings. Files live under
 * `<job>/results/`; the relativePath always begins with `results/`.
 */

export interface JobScreenshot {
  jobId: string;
  jobTitle: string;
  projectName: string;
  watchPath: string;
  fileName: string;
  /** Always begins with `results/`. */
  relativePath: string;
  /** Routable URL that serves this file (sub-path aware). */
  url: string;
  caption: string;
  /** `passed` | `failed` | `skipped` | `unknown` | null. */
  status: string | null;
  localPath: string;
  timestampUtc: string;
}

export interface JobScreenshotsResponse {
  jobId: string;
  screenshots: JobScreenshot[];
}

export interface WorkspaceScreenshotsResponse {
  windowHours: number;
  projectFilter: string | null;
  screenshots: JobScreenshot[];
}

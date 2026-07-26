import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface PromptCatalogItem {
  name: string;
  title: string;
  description: string;
  group: string;
  promptClass: 'runtime-step' | 'orchestrator' | 'drift' | 'framing';
  hasDefault: boolean;
  /** True when the application-wide user-data override exists. */
  hasGlobalOverride?: boolean;
  hasOverride: boolean;
  /** Global override drift only; project drift is carried per origin below. */
  globalDefaultChangedSinceOverride?: boolean;
  defaultChangedSinceOverride: boolean;
  slots: string[];
  usageCount: number;
  /** Project pipeline overrides bound to this runtime template. */
  projectOverrides?: PromptProjectOverride[];
  /** AGT-2286 telemetry. Optional during rolling frontend/backend upgrades. */
  lastChangedAt?: string | null;
  lastChangedSha?: string | null;
  lastReviewedAt?: string | null;
  reviewStatus: PromptReviewStatus | null;
  reviewFindingCount: number;
  projectOverrideCount: number;
  calls: PromptCallAnalytics;
}

export interface PromptCatalogResponse {
  items: PromptCatalogItem[];
  overrideDirectory: string;
  orphanedOverrides: PromptProjectOverride[];
  telemetryPath: string | null;
  deadPromptDays: number;
  costDisclaimer: string;
}

/** One recorded consumer of a template: the class + member that renders it. */
export interface PromptUsageRef {
  component: string;
  member: string;
  purpose: string;
}

export interface PromptDetail {
  name: string;
  title: string;
  description: string;
  group: string;
  promptClass: 'runtime-step' | 'orchestrator' | 'drift' | 'framing';
  hasDefault: boolean;
  hasOverride: boolean;
  defaultContent: string | null;
  overrideContent: string | null;
  baseDefaultContent: string | null;
  effectiveContent: string;
  defaultSha: string | null;
  baseDefaultSha: string | null;
  defaultChangedSinceOverride: boolean;
  overrideUpdatedAt: string | null;
  slots: string[];
  usages: PromptUsageRef[];
  lastChangedAt: string | null;
  lastChangedSha: string | null;
  review: PromptReviewMetadata | null;
  projectOverrides: PromptProjectOverride[];
  calls: PromptCallAnalytics;
  costDisclaimer: string;
}

export interface PromptCallAnalytics {
  totalCalls: number;
  calls7d: number;
  lastCalledAt: string | null;
  inputTokens: number;
  costUsd: number;
  costUsd7d: number;
  unpricedCalls: number;
  unpricedCalls7d: number;
  currentVersionCalls: number;
  isDead: boolean;
  daily: PromptCallDay[];
  versions: PromptCallVersion[];
}

export interface PromptCallDay {
  date: string;
  calls: number;
  inputTokens: number;
  costUsd: number;
}

export interface PromptCallVersion {
  version: string;
  firstCalledAt: string;
  lastCalledAt: string;
  calls: number;
  inputTokens: number;
  costUsd: number;
  unpricedCalls: number;
  isCurrent: boolean;
  models: string[];
}

export type PromptReviewStatus = 'current' | 'needs-review' | 'stale';

export interface PromptReviewFinding {
  code: string;
  severity: 'info' | 'warning' | 'error';
  message: string;
  projectName: string | null;
  stepId: string | null;
}

export interface PromptReviewMetadata {
  lastReviewedAt: string;
  reviewedBy: string;
  status: PromptReviewStatus;
  findings: PromptReviewFinding[];
}

/**
 * One project-settings pipelineSteps[step].prompt override and its shipped
 * baseline. The baseline fields let the catalogue show the same stale state
 * as the detail warning without opening every prompt.
 */
export interface PromptProjectOverride {
  projectName: string;
  stepId: string;
  promptName: string | null;
  content: string;
  orphaned: boolean;
  matchesDefault: boolean;
  addedLines: number;
  removedLines: number;
  baseDefaultSha?: string | null;
  defaultChangedSinceOverride?: boolean;
}

export interface PromptReviewResult {
  name: string;
  metadata: PromptReviewMetadata;
  projectOverrides: PromptProjectOverride[];
}

export interface PromptReviewRunResponse {
  reviewedAt: string;
  reviewedCount: number;
  findingCount: number;
  results: PromptReviewResult[];
  orphanedOverrides: PromptProjectOverride[];
}

/** Result of a non-persisting "Probelauf" render against supplied slot values. */
export interface PromptPreviewResult {
  name: string;
  rendered: string;
  slots: string[];
  filledSlots: string[];
  missingSlots: string[];
}

export interface PromptCoverageItem {
  component: string;
  status: string;
  detail: string;
}

export interface PromptCoverageResponse {
  items: PromptCoverageItem[];
  totalSites: number;
  coveredSites: number;
  pendingSites: number;
}

/**
 * Backs the system-prompt admin panel. Reads the catalog of runtime prompt
 * templates and the per-template detail (default vs override), and writes /
 * resets / re-baselines the application-wide override.
 */
@Injectable({ providedIn: 'root' })
export class PromptAdminService {
  private readonly http = inject(HttpClient);

  readonly catalog = signal<PromptCatalogResponse | null>(null);
  readonly coverage = signal<PromptCoverageResponse | null>(null);
  readonly loadError = signal<string | null>(null);

  async loadCatalog(): Promise<void> {
    try {
      const resp = await firstValueFrom(
        this.http.get<PromptCatalogResponse>('/api/admin/prompts')
      );
      this.catalog.set(resp);
      this.loadError.set(null);
    } catch (err: unknown) {
      this.loadError.set(this.describe(err, 'Failed to load prompts'));
    }
  }

  async loadCoverage(): Promise<void> {
    try {
      const resp = await firstValueFrom(
        this.http.get<PromptCoverageResponse>('/api/admin/prompts/coverage')
      );
      this.coverage.set(resp);
    } catch {
      // Coverage is a secondary read; leave the prior value and let the
      // primary catalog error surface the connectivity problem.
    }
  }

  getDetail(name: string): Promise<PromptDetail> {
    return firstValueFrom(
      this.http.get<PromptDetail>(`/api/admin/prompts/${encodeURIComponent(name)}`)
    );
  }

  preview(
    name: string,
    values: Record<string, string>,
    content?: string
  ): Promise<PromptPreviewResult> {
    return firstValueFrom(
      this.http.post<PromptPreviewResult>(
        `/api/admin/prompts/${encodeURIComponent(name)}/preview`,
        { values, content: content ?? null }
      )
    );
  }

  saveOverride(name: string, content: string): Promise<PromptDetail> {
    return firstValueFrom(
      this.http.put<PromptDetail>(`/api/admin/prompts/${encodeURIComponent(name)}`, { content })
    );
  }

  resetToDefault(name: string): Promise<PromptDetail> {
    return firstValueFrom(
      this.http.delete<PromptDetail>(`/api/admin/prompts/${encodeURIComponent(name)}`)
    );
  }

  rebaseline(name: string): Promise<PromptDetail> {
    return firstValueFrom(
      this.http.post<PromptDetail>(`/api/admin/prompts/${encodeURIComponent(name)}/rebaseline`, {})
    );
  }

  review(name: string, reviewedBy = 'prompt-admin'): Promise<PromptReviewResult> {
    return firstValueFrom(
      this.http.post<PromptReviewResult>(
        `/api/admin/prompts/${encodeURIComponent(name)}/review`,
        { reviewedBy }
      )
    );
  }

  reviewAll(reviewedBy = 'prompt-admin'): Promise<PromptReviewRunResponse> {
    return firstValueFrom(
      this.http.post<PromptReviewRunResponse>(
        '/api/admin/prompts/review-all',
        { reviewedBy }
      )
    );
  }

  private describe(err: unknown, fallback: string): string {
    if (err && typeof err === 'object') {
      const e = err as { error?: { error?: string }; message?: string };
      if (e.error?.error) return e.error.error;
      if (e.message) return e.message;
    }
    return fallback;
  }
}

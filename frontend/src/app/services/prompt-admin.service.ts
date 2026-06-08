import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface PromptCatalogItem {
  name: string;
  title: string;
  description: string;
  group: string;
  hasDefault: boolean;
  hasOverride: boolean;
  defaultChangedSinceOverride: boolean;
}

export interface PromptCatalogResponse {
  items: PromptCatalogItem[];
  overrideDirectory: string;
}

export interface PromptDetail {
  name: string;
  title: string;
  description: string;
  group: string;
  hasDefault: boolean;
  hasOverride: boolean;
  defaultContent: string | null;
  overrideContent: string | null;
  effectiveContent: string;
  defaultSha: string | null;
  baseDefaultSha: string | null;
  defaultChangedSinceOverride: boolean;
  overrideUpdatedAt: string | null;
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

  getDetail(name: string): Promise<PromptDetail> {
    return firstValueFrom(
      this.http.get<PromptDetail>(`/api/admin/prompts/${encodeURIComponent(name)}`)
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

  private describe(err: unknown, fallback: string): string {
    if (err && typeof err === 'object') {
      const e = err as { error?: { error?: string }; message?: string };
      if (e.error?.error) return e.error.error;
      if (e.message) return e.message;
    }
    return fallback;
  }
}

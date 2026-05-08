import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export type OrchestratorConfigType = 'bool' | 'int' | 'enum';

export interface OrchestratorConfigOption {
  key: string;
  group: string;
  label: string;
  description: string;
  type: OrchestratorConfigType;
  enumOptions?: string[];
  defaultValue: boolean | number | string | null;
  currentValue: boolean | number | string | null;
  hasOverride: boolean;
  restartRequired: boolean;
  sourceFile: string;
}

export interface OrchestratorConfigSnapshot {
  options: OrchestratorConfigOption[];
  overrideFilePath: string;
  overrideFileExists: boolean;
}

/**
 * Backs the Orchestrator config drawer. Reads the typed catalog +
 * current values from the backend, applies a partial override map,
 * and exposes a `pendingRestart` signal so the panel can render the
 * "Restart required" banner across reloads of the panel.
 */
@Injectable({ providedIn: 'root' })
export class OrchestratorConfigService {
  private readonly http = inject(HttpClient);

  readonly snapshot = signal<OrchestratorConfigSnapshot | null>(null);
  readonly pendingRestart = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);

  async load(): Promise<void> {
    try {
      const snap = await firstValueFrom(
        this.http.get<OrchestratorConfigSnapshot>('/api/admin/config/orchestrator')
      );
      this.snapshot.set(snap);
      this.loadError.set(null);
    } catch (err: unknown) {
      this.loadError.set(this.describe(err));
    }
  }

  async update(values: Record<string, boolean | number | string>): Promise<void> {
    const snap = await firstValueFrom(
      this.http.put<OrchestratorConfigSnapshot>('/api/admin/config/orchestrator', { values })
    );
    this.snapshot.set(snap);
    this.pendingRestart.set(true);
  }

  acknowledgeRestart(): void {
    this.pendingRestart.set(false);
  }

  private describe(err: unknown): string {
    if (err && typeof err === 'object' && 'message' in err) {
      return String((err as { message?: unknown }).message ?? 'Failed to load orchestrator config');
    }
    return 'Failed to load orchestrator config';
  }
}

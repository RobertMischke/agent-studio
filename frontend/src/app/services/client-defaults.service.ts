import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { CLIENT_ID } from './client-id.interceptor';
import type { CliType } from '../models/task.model';
import { CLI_TYPES, ClientDefaultsResponse } from '../models/task.model';

/**
 * Two-way sync of the user's preferred default CLI + model between
 * localStorage (fast read cache used by the create-task dialog and status
 * bar on app boot) and the backend `/api/clients/{id}/defaults` endpoint
 * (durable source of truth shared with the orchestrator chat).
 *
 * Why both? The status bar and create dialog read defaults synchronously
 * during render; jumping to the network on every read would block the UI.
 * The backend copy is what the orchestrator reads on every chat turn so a
 * "create me three tasks" request lands on the user's real default
 * instead of a hardcoded fallback (F17).
 *
 * Flow:
 *   - On app boot, `hydrate()` reads the API once and writes the result
 *     into localStorage, so the next render picks up the persisted value
 *     even if the user changed it from a different session.
 *   - On user change (status-bar dropdowns), `setDefaultCli` /
 *     `setDefaultModel` write localStorage immediately and fire a
 *     best-effort PUT in the background. PUT failures only warn — the
 *     local change still applies.
 */
@Injectable({ providedIn: 'root' })
export class ClientDefaultsService {
  private readonly http = inject(HttpClient);

  private static readonly STORAGE_DEFAULT_CLI = 'defaultCliType';
  private static readonly STORAGE_DEFAULT_MODEL_PREFIX = 'defaultModel:';

  /** Pull current defaults from the backend and seed localStorage. */
  async hydrate(): Promise<void> {
    try {
      const r = await firstValueFrom(
        this.http.get<ClientDefaultsResponse>(`/api/clients/${encodeURIComponent(CLIENT_ID)}/defaults`)
      );
      if (!r) return;
      if (r.defaultCliType && (CLI_TYPES as string[]).includes(r.defaultCliType)) {
        localStorage.setItem(ClientDefaultsService.STORAGE_DEFAULT_CLI, r.defaultCliType);
      }
      if (r.defaultModel) {
        const cli = (r.defaultCliType && (CLI_TYPES as string[]).includes(r.defaultCliType))
          ? r.defaultCliType
          : (localStorage.getItem(ClientDefaultsService.STORAGE_DEFAULT_CLI) ?? 'claude');
        localStorage.setItem(ClientDefaultsService.STORAGE_DEFAULT_MODEL_PREFIX + cli, r.defaultModel);
      }
    } catch {
      // Backend unreachable on boot is non-fatal; localStorage is the
      // cache and the status bar will still render last-known values.
    }
  }

  /** Push the new default CLI to the backend; best-effort. */
  async pushDefaultCli(cli: CliType): Promise<void> {
    try {
      await firstValueFrom(
        this.http.put<ClientDefaultsResponse>(
          `/api/clients/${encodeURIComponent(CLIENT_ID)}/defaults`,
          { defaultCliType: cli }
        )
      );
    } catch {
      // ignored: localStorage already updated
    }
  }

  /** Push the new default model id (or clear it via empty string). */
  async pushDefaultModel(model: string): Promise<void> {
    try {
      await firstValueFrom(
        this.http.put<ClientDefaultsResponse>(
          `/api/clients/${encodeURIComponent(CLIENT_ID)}/defaults`,
          { defaultModel: model ?? '' }
        )
      );
    } catch {
      // ignored
    }
  }
}

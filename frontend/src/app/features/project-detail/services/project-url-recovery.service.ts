import { Injectable, inject, signal } from '@angular/core';
import { map, type Observable } from 'rxjs';
import type { ProjectUrlSuggestion, RegistryProjectUrl } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';

/** Detects and applies a safe repository-derived URL Preview start rule. */
@Injectable({ providedIn: 'root' })
export class ProjectUrlRecoveryService {
  private readonly tasks = inject(TaskService);
  private readonly quickSetupRequest = signal<{
    urlId: string;
    suggestion: ProjectUrlSuggestion | null;
  } | null>(null);

  /** Carry the failing URL and any already-detected safe suggestion across the
   * preview-to-settings tab transition. The Settings panel consumes this once. */
  requestQuickSetup(urlId: string, suggestion: ProjectUrlSuggestion | null): void {
    this.quickSetupRequest.set({ urlId, suggestion });
  }

  takeQuickSetupRequest(): { urlId: string; suggestion: ProjectUrlSuggestion | null } | null {
    const request = this.quickSetupRequest();
    this.quickSetupRequest.set(null);
    return request;
  }

  detect(projectId: string, url: RegistryProjectUrl): Observable<ProjectUrlSuggestion | null> {
    return this.tasks.getProjectUrlSuggestions(projectId).pipe(map(suggestions => {
      const configuredPort = url.startRule?.port ?? this.portFrom(url.url);
      return suggestions.find(value => value.port === configuredPort) ?? suggestions[0] ?? null;
    }));
  }

  apply(projectId: string, url: RegistryProjectUrl, suggestion: ProjectUrlSuggestion): Observable<RegistryProjectUrl | null> {
    return this.tasks.updateProjectUrl(projectId, url.id, {
      label: url.label,
      url: url.url,
      startRule: {
        command: suggestion.command,
        cwd: suggestion.cwd,
        port: suggestion.port,
        healthUrl: suggestion.url ?? url.url,
        readinessTimeoutSeconds: 20,
        source: suggestion.source,
      },
    }).pipe(map(project => project.urls.find(item => item.id === url.id) ?? null));
  }

  private portFrom(raw: string): number | null {
    try {
      const parsed = new URL(raw);
      return parsed.port ? Number(parsed.port) : parsed.protocol === 'https:' ? 443 : 80;
    } catch { return null; }
  }
}

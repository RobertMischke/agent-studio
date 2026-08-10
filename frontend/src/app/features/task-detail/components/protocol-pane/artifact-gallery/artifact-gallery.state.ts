import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import type { ConversationArtifact } from './artifact-gallery.model';

interface ArtifactPreviewState {
  readonly expanded: boolean;
  readonly loading: boolean;
  readonly content?: string | null;
}

/** Keeps document previews stable while the polled conversation DOM is reconciled. */
@Injectable({ providedIn: 'root' })
export class ArtifactGalleryState {
  private readonly http = inject(HttpClient);
  private readonly states = signal<ReadonlyMap<string, ArtifactPreviewState>>(new Map());
  private readonly pending = new Map<string, Promise<void>>();

  isExpanded(artifact: ConversationArtifact): boolean {
    return this.stateFor(artifact).expanded;
  }

  isLoading(artifact: ConversationArtifact): boolean {
    return this.stateFor(artifact).loading;
  }

  contentFor(artifact: ConversationArtifact): string | null | undefined {
    return this.stateFor(artifact).content;
  }

  toggle(artifact: ConversationArtifact): boolean {
    const current = this.stateFor(artifact);
    const expanded = !current.expanded;
    this.setState(artifact, { ...current, expanded });
    return expanded;
  }

  load(artifact: ConversationArtifact): Promise<void> {
    const key = previewKey(artifact);
    const current = this.stateFor(artifact);
    if (current.content !== undefined) return Promise.resolve();
    const active = this.pending.get(key);
    if (active) return active;
    if (!artifact.contentUrl) return Promise.resolve();

    this.setState(artifact, { ...current, loading: true });
    const request = new Promise<void>((resolve) => {
      this.http.get(artifact.contentUrl!, { responseType: 'text' }).subscribe({
        next: (body) => {
          this.setState(artifact, {
            ...this.stateFor(artifact),
            loading: false,
            content: body,
          });
          resolve();
        },
        error: () => {
          this.setState(artifact, {
            ...this.stateFor(artifact),
            loading: false,
            content: null,
          });
          resolve();
        },
      });
    }).finally(() => this.pending.delete(key));
    this.pending.set(key, request);
    return request;
  }

  private stateFor(artifact: ConversationArtifact): ArtifactPreviewState {
    return this.states().get(previewKey(artifact)) ?? { expanded: false, loading: false };
  }

  private setState(artifact: ConversationArtifact, state: ArtifactPreviewState): void {
    const next = new Map(this.states());
    next.set(previewKey(artifact), state);
    this.states.set(next);
  }
}

function previewKey(artifact: ConversationArtifact): string {
  return artifact.contentUrl ?? `${artifact.kind}:${artifact.path}`;
}

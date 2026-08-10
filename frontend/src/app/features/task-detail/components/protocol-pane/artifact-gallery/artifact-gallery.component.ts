import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { copyTextToClipboard } from '../../../../../services/clipboard.util';
import { MediaLightboxService } from '../../../../../services/media-lightbox.service';
import type { ConversationArtifact } from './artifact-gallery.model';
import { ArtifactGalleryState } from './artifact-gallery.state';

interface DiffPreviewLine {
  readonly id: string;
  readonly text: string;
  readonly kind: 'add' | 'delete' | 'hunk' | 'meta' | 'context';
}

@Component({
  selector: 'app-artifact-gallery',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownViewComponent, TooltipDirective],
  templateUrl: './artifact-gallery.component.html',
  styleUrl: './artifact-gallery.component.scss',
})
export class ArtifactGalleryComponent {
  readonly artifacts = input.required<readonly ConversationArtifact[]>();

  private readonly lightbox = inject(MediaLightboxService);
  private readonly previewState = inject(ArtifactGalleryState);
  private readonly copyStates = signal<ReadonlyMap<string, 'idle' | 'copied' | 'failed'>>(new Map());

  readonly images = computed(() => this.artifacts().filter((artifact) => artifact.kind === 'image'));
  readonly documents = computed(() => this.artifacts().filter((artifact) => artifact.kind !== 'image'));
  readonly heading = computed(() => {
    const images = this.images().length;
    const documents = this.documents().length;
    const parts = [
      images > 0 ? `${images} ${images === 1 ? 'image' : 'images'}` : '',
      documents > 0 ? `${documents} ${documents === 1 ? 'document' : 'documents'}` : '',
    ].filter(Boolean);
    return `Artifacts · ${parts.join(' · ')}`;
  });

  openImage(selected: ConversationArtifact): void {
    const images = this.images();
    const index = Math.max(0, images.findIndex((artifact) => artifact.id === selected.id));
    this.lightbox.openGallery({
      index,
      images: images.map((artifact) => ({
        src: artifact.url,
        alt: `${artifact.fileName} · ${artifact.path}`,
        actions: [{
          id: 'open-new-tab',
          label: 'Open in new tab',
          tooltip: `Open ${artifact.path} in a new tab`,
          run: () => this.openInNewTab(artifact),
        }],
      })),
    });
  }

  toggleDocument(artifact: ConversationArtifact): void {
    if (artifact.kind === 'html') {
      this.openInNewTab(artifact);
      return;
    }
    if (this.previewState.toggle(artifact)) void this.previewState.load(artifact);
  }

  isExpanded(artifact: ConversationArtifact): boolean {
    return this.previewState.isExpanded(artifact);
  }

  isLoading(artifact: ConversationArtifact): boolean {
    return this.previewState.isLoading(artifact);
  }

  contentFor(artifact: ConversationArtifact): string | null | undefined {
    return this.previewState.contentFor(artifact);
  }

  displayContent(artifact: ConversationArtifact): string {
    const raw = this.previewState.contentFor(artifact) ?? '';
    if (artifact.kind !== 'json') return raw;
    try {
      return JSON.stringify(JSON.parse(raw), null, 2);
    } catch {
      return raw;
    }
  }

  diffLines(artifact: ConversationArtifact): readonly DiffPreviewLine[] {
    return this.displayContent(artifact).split('\n').map((text, index) => ({
      id: `${artifact.id}:${index}`,
      text,
      kind: diffLineKind(text),
    }));
  }

  typeLabel(artifact: ConversationArtifact): string {
    switch (artifact.kind) {
      case 'diff': return 'DIFF';
      case 'markdown': return 'MD';
      case 'html': return 'HTML';
      case 'json': return 'JSON';
      case 'log': return 'LOG';
      default: return 'FILE';
    }
  }

  rowActionLabel(artifact: ConversationArtifact): string {
    if (artifact.kind === 'html') return 'Open viewer';
    return this.isExpanded(artifact) ? 'Collapse' : 'Preview';
  }

  copyLabel(id: string): string {
    const state = this.copyStates().get(id) ?? 'idle';
    if (state === 'copied') return 'Copied';
    if (state === 'failed') return 'Copy failed';
    return 'Copy';
  }

  async copyDocument(artifact: ConversationArtifact, event: Event): Promise<void> {
    event.stopPropagation();
    await this.previewState.load(artifact);
    const ok = await copyTextToClipboard(this.displayContent(artifact));
    this.setCopyState(artifact.id, ok ? 'copied' : 'failed');
    setTimeout(() => this.setCopyState(artifact.id, 'idle'), 1800);
  }

  openInNewTab(artifact: ConversationArtifact, event?: Event): void {
    event?.stopPropagation();
    if (typeof window !== 'undefined') window.open(artifact.url, '_blank', 'noopener,noreferrer');
  }

  private setCopyState(id: string, state: 'idle' | 'copied' | 'failed'): void {
    const next = new Map(this.copyStates());
    next.set(id, state);
    this.copyStates.set(next);
  }

}

function diffLineKind(text: string): DiffPreviewLine['kind'] {
  if (text.startsWith('@@')) return 'hunk';
  if (text.startsWith('diff ') || text.startsWith('index ') || text.startsWith('--- ') || text.startsWith('+++ ')) return 'meta';
  if (text.startsWith('+')) return 'add';
  if (text.startsWith('-')) return 'delete';
  return 'context';
}

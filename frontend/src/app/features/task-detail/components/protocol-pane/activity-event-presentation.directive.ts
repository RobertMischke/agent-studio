import {
  AfterViewChecked,
  ApplicationRef,
  ComponentRef,
  Directive,
  ElementRef,
  EnvironmentInjector,
  OnDestroy,
  createComponent,
  inject,
  input,
} from '@angular/core';
import type { ConversationEvent } from 'coding-agent-chat/core';
import {
  isPresentedToolBurstEvent,
  type PresentedToolBurstEvent,
} from './activity-event-presentation';
import { ArtifactGalleryComponent } from './artifact-gallery/artifact-gallery.component';
import {
  artifactBlocks,
  type ConversationArtifact,
  type ConversationArtifactBlock,
} from './artifact-gallery/artifact-gallery.model';

interface GalleryMount {
  readonly host: HTMLElement;
  readonly ref: ComponentRef<ArtifactGalleryComponent>;
  signature: string;
}

/**
 * Compatibility adapter for compact metadata that coding-agent-chat 0.3.2
 * does not render yet. The normal ToolBurstEvent stays intact, including its
 * expandable details; this directive only enriches the compact Studio row.
 */
@Directive({
  selector: 'cac-conversation-view[appActivityEventPresentation]',
  standalone: true,
})
export class ActivityEventPresentationDirective implements AfterViewChecked, OnDestroy {
  readonly events = input.required<readonly ConversationEvent[]>({
    alias: 'appActivityEventPresentation',
  });

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly applicationRef = inject(ApplicationRef);
  private readonly environmentInjector = inject(EnvironmentInjector);
  private readonly galleries = new Map<string, GalleryMount>();

  ngAfterViewChecked(): void {
    this.syncToolBursts();
    this.syncArtifactBlocks();
  }

  ngOnDestroy(): void {
    for (const mount of this.galleries.values()) this.destroyGallery(mount);
    this.galleries.clear();
  }

  private syncToolBursts(): void {
    const events = this.events().filter(isPresentedToolBurstEvent);
    const chips = this.host.nativeElement.querySelectorAll<HTMLElement>(
      '[data-testid="tool-burst-chip"]',
    );

    chips.forEach((chip, index) => {
      const event = events[index];
      if (event) this.syncChip(chip, event);
    });
  }

  private syncArtifactBlocks(): void {
    const rows = [...this.host.nativeElement.querySelectorAll<HTMLElement>(
      '[data-testid="conversation-artifact-image"]',
    )];
    rows.forEach((row) => {
      row.hidden = false;
      row.removeAttribute('aria-hidden');
    });

    const blocks = artifactBlocks(this.events());
    const activeIds = new Set<string>();
    for (const block of blocks) {
      const blockRows = rows.slice(block.startOrdinal, block.startOrdinal + block.rowCount);
      if (blockRows.length !== block.rowCount || blockRows.length === 0) continue;
      activeIds.add(block.id);
      blockRows.forEach((row) => {
        row.hidden = true;
        row.setAttribute('aria-hidden', 'true');
      });
      this.mountGallery(block, blockRows[0]);
    }

    for (const [id, mount] of this.galleries) {
      if (activeIds.has(id)) continue;
      this.destroyGallery(mount);
      this.galleries.delete(id);
    }
  }

  private mountGallery(block: ConversationArtifactBlock, firstRow: HTMLElement): void {
    const signature = artifactSignature(block.artifacts);
    let mount = this.galleries.get(block.id);
    if (mount && !mount.host.isConnected) {
      this.destroyGallery(mount);
      this.galleries.delete(block.id);
      mount = undefined;
    }
    if (!mount) {
      const host = firstRow.ownerDocument.createElement('li');
      host.className = 'conv__row conv__row--artifact-gallery';
      host.dataset['testid'] = 'conversation-artifact-gallery-host';
      const ref = createComponent(ArtifactGalleryComponent, {
        environmentInjector: this.environmentInjector,
        hostElement: host,
      });
      this.applicationRef.attachView(ref.hostView);
      mount = { host, ref, signature: '' };
      this.galleries.set(block.id, mount);
    }

    firstRow.parentElement?.insertBefore(mount.host, firstRow);
    if (mount.signature !== signature) {
      mount.ref.setInput('artifacts', block.artifacts);
      mount.ref.changeDetectorRef.detectChanges();
      mount.signature = signature;
    }
  }

  private destroyGallery(mount: GalleryMount): void {
    this.applicationRef.detachView(mount.ref.hostView);
    mount.ref.destroy();
    mount.host.remove();
  }

  private syncChip(chip: HTMLElement, event: PresentedToolBurstEvent): void {
    chip.dataset['activityEventId'] = event.id;
    const row = chip.querySelector<HTMLElement>('[data-testid="tool-burst-row"]');
    const count = row?.querySelector<HTMLElement>('[data-testid="tool-burst-count"]');
    const summary = count?.parentElement;
    if (!row || !count || !summary) return;

    row.dataset['activitySummary'] = event.rowPresentation.kind;
    count.textContent = event.rowPresentation.primaryLabel;

    const failure = summary.querySelector<HTMLElement>('[data-testid="tool-burst-failures"]');
    this.syncSummaryPart(summary, failure, 'activity-tool-mix', event.rowPresentation.mixLabel);
    const filePart = this.syncSummaryPart(
      summary,
      failure,
      'activity-edit-files',
      event.rowPresentation.pathLabel,
    );
    if (filePart && event.rowPresentation.fileTooltip) {
      filePart.title = event.rowPresentation.fileTooltip;
      filePart.setAttribute(
        'aria-label',
        `Edited files: ${event.rowPresentation.fileTooltip.replace(/\n/g, ', ')}`,
      );
    }

    if (failure) {
      failure.textContent = `· ${event.rowPresentation.outcomeLabel}`;
      this.removeSummaryPart(summary, 'activity-tool-outcome');
    } else {
      this.syncSummaryPart(
        summary,
        null,
        'activity-tool-outcome',
        event.rowPresentation.outcomeLabel,
      );
    }

    const expandedFiles = chip.querySelectorAll<HTMLElement>(
      '[data-testid="tool-burst-files"] code',
    );
    expandedFiles.forEach((file, index) => {
      const detail = event.fileDetails?.[index];
      if (!detail) return;
      file.title = detail.fullPath;
      file.setAttribute('aria-label', `${detail.displayPath}. Full path: ${detail.fullPath}`);
    });
  }

  private syncSummaryPart(
    summary: HTMLElement,
    before: HTMLElement | null,
    testId: string,
    label: string | undefined,
  ): HTMLElement | null {
    let part = summary.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
    if (!label) {
      part?.remove();
      return null;
    }
    if (!part) {
      part = summary.ownerDocument.createElement('span');
      part.dataset['testid'] = testId;
      summary.insertBefore(part, before);
    }
    part.textContent = `· ${label}`;
    return part;
  }

  private removeSummaryPart(summary: HTMLElement, testId: string): void {
    summary.querySelector<HTMLElement>(`[data-testid="${testId}"]`)?.remove();
  }
}

function artifactSignature(artifacts: readonly ConversationArtifact[]): string {
  return artifacts
    .map((artifact) => `${artifact.id}|${artifact.kind}|${artifact.path}|${artifact.url}|${artifact.thumbnailUrl ?? ''}`)
    .join('\n');
}

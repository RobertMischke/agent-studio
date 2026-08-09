import { AfterViewChecked, Directive, ElementRef, inject, input } from '@angular/core';
import type { ConversationEvent } from 'coding-agent-chat/core';
import {
  isPresentedToolBurstEvent,
  type PresentedToolBurstEvent,
} from './activity-event-presentation';

/**
 * Compatibility adapter for compact metadata that coding-agent-chat 0.3.2
 * does not render yet. The normal ToolBurstEvent stays intact, including its
 * expandable details; this directive only enriches the compact Studio row.
 */
@Directive({
  selector: 'cac-conversation-view[appActivityEventPresentation]',
  standalone: true,
})
export class ActivityEventPresentationDirective implements AfterViewChecked {
  readonly events = input.required<readonly ConversationEvent[]>({
    alias: 'appActivityEventPresentation',
  });

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  ngAfterViewChecked(): void {
    const events = this.events().filter(isPresentedToolBurstEvent);
    const chips = this.host.nativeElement.querySelectorAll<HTMLElement>(
      '[data-testid="tool-burst-chip"]',
    );

    chips.forEach((chip, index) => {
      const event = events[index];
      if (event) this.syncChip(chip, event);
    });
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

import { AfterViewChecked, Directive, ElementRef, inject, input } from '@angular/core';
import type { ConversationEvent } from 'coding-agent-chat/core';
import { isActivitySummaryEvent } from './activity-event-presentation';

/**
 * Host compatibility adapter for summary metadata that coding-agent-chat
 * 0.3.2 does not render yet. The projection remains the source of the row
 * content; this directive adds the full-path disclosure and redirects the
 * existing row action label until the 0.4.0 library row replaces it.
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
    const summaries = this.events().filter(isActivitySummaryEvent);
    const rows = this.host.nativeElement.querySelectorAll<HTMLElement>(
      '[data-category="activity-tool-summary"], [data-category="activity-edit-summary"]',
    );

    rows.forEach((row, index) => {
      const summary = summaries[index];
      if (!summary) return;
      const presentation = summary.activityPresentation;
      row.dataset['activitySummary'] = presentation.kind;
      row.dataset['activityEventId'] = summary.id;

      if (presentation.kind !== 'edit') return;
      const pathTarget = row.querySelector<HTMLElement>('.status-row__text');
      if (pathTarget && presentation.fullPaths.length > 0) {
        pathTarget.title = presentation.fullPaths.join('\n');
        pathTarget.setAttribute('aria-label', `Edited files: ${presentation.fullPaths.join(', ')}`);
        pathTarget.dataset['testid'] = 'activity-edit-files';
      }

      if (presentation.action !== 'commit-diff') return;
      const action = row.querySelector<HTMLButtonElement>('.status-row__trace');
      if (!action) return;
      action.textContent = 'commit diff';
      action.title = 'Open the existing run commit diff';
      action.setAttribute('aria-label', 'Open commit diff');
      action.dataset['activityAction'] = 'commit-diff';
    });
  }
}

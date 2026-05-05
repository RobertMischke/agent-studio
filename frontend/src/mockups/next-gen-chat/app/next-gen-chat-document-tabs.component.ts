import { Component, input, output } from '@angular/core';

import { ICON_PATHS } from './next-gen-chat-workbench-prototype.data';
import { WorkbenchDocument, WorkbenchDocumentId } from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'mockup-next-gen-chat-document-tabs',
  standalone: true,
  template: `
    <div class="document-tabs" data-testid="prototype-document-tabs" role="tablist" aria-label="Open workbench documents">
      @for (doc of documents(); track doc.id) {
        <button class="document-tab"
                type="button"
                role="tab"
                [class.document-tab--active]="activeDocument() === doc.id"
                [attr.aria-selected]="activeDocument() === doc.id"
                [attr.data-testid]="'prototype-document-' + doc.id"
                (click)="documentActivated.emit(doc.id)">
          <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
            @for (path of iconPath(doc.icon); track path) {
              <path [attr.d]="path"></path>
            }
          </svg>
          <span>{{ doc.title }}</span>
          <em>{{ doc.subtitle }}</em>
          @if (doc.closable) {
            <span class="document-tab__close"
                  title="Close document"
                  aria-label="Close document"
                  role="button"
                  tabindex="-1"
                  (click)="documentClosed.emit(doc.id); $event.stopPropagation()">x</span>
          }
        </button>
      }
    </div>
  `,
})
export class NextGenChatDocumentTabsComponent {
  readonly documents = input.required<readonly WorkbenchDocument[]>();
  readonly activeDocument = input.required<WorkbenchDocumentId>();

  readonly documentActivated = output<WorkbenchDocumentId>();
  readonly documentClosed = output<WorkbenchDocumentId>();

  iconPath(name: string): string[] {
    return ICON_PATHS[name] ?? ICON_PATHS['help'];
  }
}

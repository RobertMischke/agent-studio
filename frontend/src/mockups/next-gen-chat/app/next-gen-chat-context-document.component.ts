import { Component, computed, input, output } from '@angular/core';

import { ICON_PATHS } from './next-gen-chat-workbench-prototype.data';
import {
  ContextPane,
  FeatureAction,
  FeatureParityItem,
  GitFileRow,
  TokenUsageRow,
  WorkbenchPane,
} from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'mockup-next-gen-chat-context-document',
  standalone: true,
  templateUrl: './next-gen-chat-context-document.component.html',
})
export class NextGenChatContextDocumentComponent {
  readonly pane = input.required<ContextPane>();
  readonly chatOpen = input.required<boolean>();
  readonly featureParity = input.required<readonly FeatureParityItem[]>();
  readonly gitFiles = input.required<readonly GitFileRow[]>();
  readonly activeGitFile = input.required<string>();
  readonly screenshots = input.required<readonly string[]>();
  readonly tokenRows = input.required<readonly TokenUsageRow[]>();

  readonly toggleChatRequested = output<void>();
  readonly closePaneRequested = output<ContextPane>();
  readonly debugRequested = output<void>();
  readonly paneSelected = output<WorkbenchPane>();
  readonly featureSelected = output<FeatureAction>();
  readonly activeGitFileChanged = output<string>();
  readonly lightboxRequested = output<void>();

  readonly selectedGitFile = computed(() =>
    this.gitFiles().find((file) => file.path === this.activeGitFile()) ?? this.gitFiles()[0]
  );

  paneTitle(): string {
    switch (this.pane()) {
      case 'git': return 'Git changes';
      case 'preview': return 'Screenshot preview';
      case 'debug': return 'Debug summary';
      default: return 'Result summary';
    }
  }

  paneSubtitle(): string {
    switch (this.pane()) {
      case 'git': return 'Changed files, commits, source diff';
      case 'preview': return 'Durable visual evidence and lightbox';
      case 'debug': return 'Tokens, actors, waits, and raw links';
      default: return 'Human-readable outcome and risk signals';
    }
  }

  iconPath(name: string): string[] {
    return ICON_PATHS[name] ?? ICON_PATHS['help'];
  }
}

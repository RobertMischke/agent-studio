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
  template: `
    <aside class="context"
           aria-label="Workbench context pane"
           [attr.data-pane]="pane()"
           [attr.data-testid]="'prototype-pane-' + pane() + '-view'">
      <header class="context__head">
        <div>
          <strong>{{ paneTitle() }}</strong>
          <span>{{ paneSubtitle() }}</span>
        </div>
        <div>
          <button class="icon-btn"
                  type="button"
                  (click)="toggleChatRequested.emit()"
                  [title]="chatOpen() ? 'Close chat' : 'Open chat'"
                  [attr.aria-label]="chatOpen() ? 'Close chat' : 'Open chat'"
                  data-testid="prototype-chat-toggle">
            <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
              @for (path of iconPath(chatOpen() ? 'panelClose' : 'panelOpen'); track path) {
                <path [attr.d]="path"></path>
              }
            </svg>
          </button>
          @if (pane() !== 'result') {
            <button class="icon-btn"
                    type="button"
                    (click)="closePaneRequested.emit(pane())"
                    [title]="'Close ' + paneTitle()"
                    [attr.aria-label]="'Close ' + paneTitle()"
                    [attr.data-testid]="'prototype-pane-' + pane() + '-close'">
              <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                @for (path of iconPath('close'); track path) {
                  <path [attr.d]="path"></path>
                }
              </svg>
            </button>
          }
          <button class="icon-btn" type="button" (click)="debugRequested.emit()" title="Open Verbose Debug" aria-label="Open Verbose Debug">
            <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
              @for (path of iconPath('bug'); track path) {
                <path [attr.d]="path"></path>
              }
            </svg>
          </button>
        </div>
      </header>

      @switch (pane()) {
        @case ('result') {
          <section class="context__body">
            <article class="summary-callout" data-testid="prototype-summary-document">
              <span>Default document</span>
              <strong>Review ready</strong>
              <p>Start here for phase, risk, evidence, and the next useful document. Each tab owns a full document surface; split review is an explicit action, not the default layout.</p>
              <div>
                <button type="button" (click)="paneSelected.emit('git')">Open Git document</button>
                <button type="button" (click)="paneSelected.emit('chat')">Open Chat document</button>
                <button type="button" (click)="paneSelected.emit('debug')">Open debug document</button>
              </div>
            </article>
            <div class="metric-grid">
              <div><b>Review</b><span>current state</span></div>
              <div><b>3</b><span>commits</span></div>
              <div><b>4</b><span>screenshots</span></div>
              <div><b>42k</b><span>tokens</span></div>
            </div>
            <article class="context-card">
              <h3>Human result</h3>
              <p>The renderer turns noisy Activity Logs into a readable conversation, keeps the side sheet for project steering, and opens adjacent review panes only when useful.</p>
            </article>
            <article class="context-card">
              <h3>Acceptance snapshot</h3>
              <p>Preserve Trace, run timeline, Files, Commits, Screenshots, token surfaces, composer behavior, and side-sheet controls while the flag is off.</p>
            </article>
            <article class="context-card">
              <h3>Existing functions carried forward</h3>
              <div class="function-grid">
                @for (item of featureParity(); track item.label) {
                  <button type="button" [attr.title]="item.note" (click)="featureSelected.emit(item.action)">
                    <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                      @for (path of iconPath(item.icon); track path) {
                        <path [attr.d]="path"></path>
                      }
                    </svg>
                    <span>{{ item.label }}</span>
                  </button>
                }
              </div>
            </article>
          </section>
        }

        @case ('git') {
          <section class="context__body">
            <div class="git-split">
              <div>
                <div class="git-summary">
                  <b>3 commits</b>
                  <span>8 files · +624 -91 · no unreviewed conflict</span>
                </div>
                @for (file of gitFiles(); track file.path) {
                  <button class="file-row"
                          type="button"
                          [class.task-card--active]="activeGitFile() === file.path"
                          (click)="activeGitFileChanged.emit(file.path)">
                    <code>{{ file.path }}</code>
                    <span>{{ file.delta }}</span>
                  </button>
                }
                <div class="git-actions">
                  <button type="button" (click)="paneSelected.emit('chat')">Open Chat tab</button>
                  <button type="button" (click)="paneSelected.emit('result')">Open Summary tab</button>
                  <button type="button" (click)="paneSelected.emit('preview')">Open Screenshots tab</button>
                </div>
              </div>
              <article class="source-card" data-testid="prototype-git-editor">
                <strong>Source editor / diff</strong>
                <span>{{ activeGitFile() }}</span>
                <code>{{ selectedGitFile().delta }} · staged preview</code>
                <pre>@@ next-gen chat workbench
+ Chat can be closed as an optional pane.
+ Git changes own the source editor and diff preview.
+ Split width is controlled by the vertical workbench splitter.</pre>
              </article>
            </div>
          </section>
        }

        @case ('preview') {
          <section class="context__body">
            <div class="preview-grid">
              @for (shot of screenshots(); track shot) {
                <button type="button" (click)="lightboxRequested.emit()">
                  <span></span>
                  <b>{{ shot }}</b>
                </button>
              }
            </div>
            <article class="context-card">
              <h3>Visual evidence</h3>
              <p>Screenshot evidence appears only when review depends on it. Durable result paths are distinct from Playwright scratch output.</p>
            </article>
          </section>
        }

        @case ('debug') {
          <section class="context__body">
            <div class="token-bars">
              @for (row of tokenRows(); track row.name) {
                <div>
                  <span>{{ row.name }}</span>
                  <i><em [style.width.%]="row.percent"></em></i>
                  <b>{{ row.value }}</b>
                </div>
              }
            </div>
            <article class="context-card">
              <h3>Actor activity</h3>
              <p>Agent 7 turns, Orchestrator 2 decisions, QA 1 report, Supervisor 1 wait advisory. Full causality opens in Verbose Debug.</p>
            </article>
          </section>
        }
      }
    </aside>
  `,
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

import { Component, computed, signal } from '@angular/core';

import { FoundNextStatusbarComponent } from './found-next-statusbar.component';
import { FoundNextTopbarComponent } from './found-next-topbar.component';
import { NextGenChatActivityBarComponent } from './next-gen-chat-activity-bar.component';
import { NextGenChatContextDocumentComponent } from './next-gen-chat-context-document.component';
import { NextGenChatDocumentTabsComponent } from './next-gen-chat-document-tabs.component';
import { NextGenChatQueueComponent } from './next-gen-chat-queue.component';
import { NextGenChatRailComponent } from './next-gen-chat-rail.component';
import { ICON_PATHS, PROJECT_TABS, USAGE_STRIP } from './next-gen-chat-workbench-prototype.data';
import {
  ActivityTarget,
  ActivityItem,
  ActorKind,
  ActorMeta,
  ComposeMode,
  ContextPane,
  DebugTab,
  DecisionEntry,
  DecisionKind,
  Density,
  FeatureParityItem,
  FeatureAction,
  GitFileRow,
  InterventionTarget,
  PaneButton,
  Scenario,
  ScenarioOption,
  StatusPanel,
  SummaryChip,
  TaskQueueCard,
  Theme,
  TranscriptEntry,
  TokenUsageRow,
  WorkbenchDocument,
  WorkbenchDocumentId,
  WorkbenchPane,
} from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'app-next-gen-chat-workbench-prototype',
  standalone: true,
  imports: [
    FoundNextTopbarComponent,
    FoundNextStatusbarComponent,
    NextGenChatActivityBarComponent,
    NextGenChatContextDocumentComponent,
    NextGenChatDocumentTabsComponent,
    NextGenChatQueueComponent,
    NextGenChatRailComponent,
  ],
  template: `
    @if (!closed()) {
      <section class="ng-chat-prototype"
               [attr.data-theme]="theme()"
               [attr.data-density]="density()"
               [attr.data-pane]="activeDocument()"
               [attr.data-queue-open]="queueOpen()"
               data-testid="next-gen-chat-angular-prototype">
      <mockup-next-gen-chat-activity-bar
        [items]="activityItems"
        [activeActivity]="activeActivity()"
        (activitySelected)="handleActivity($event)"
        (closeRequested)="close()">
      </mockup-next-gen-chat-activity-bar>

      <app-found-next-topbar
        [theme]="theme()"
        [density]="density()"
        [sideSheetOpen]="sideSheetOpen()"
        [statusPanel]="statusPanel()"
        (projectPanelRequested)="toggleStatusPanel('projects')"
        (sideSheetToggled)="sideSheetOpen.set(!sideSheetOpen())"
        (queuePanelRequested)="toggleStatusPanel('queue')"
        (densityToggled)="toggleDensity()"
        (themeToggled)="toggleTheme()"
        (commandRequested)="commandOpen.set(true)"
        (debugRequested)="debugOpen.set(true)"
        (closeRequested)="close()">
      </app-found-next-topbar>

      <main class="workspace"
            [class.workspace--queue-closed]="!queueOpen()"
            [class.workspace--sheet-closed]="!sideSheetOpen()">
        @if (queueOpen()) {
          <mockup-next-gen-chat-queue
            [tasks]="taskCards"
            (closeRequested)="closeQueueModule()">
          </mockup-next-gen-chat-queue>
        }

        <section class="detail" aria-label="Task detail">
          <header class="detail-chrome" data-testid="prototype-detail-chrome">
            <button class="detail-chrome__back" title="Back to board" aria-label="Back to board">
              <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                @for (path of iconPath('back'); track path) {
                  <path [attr.d]="path"></path>
                }
              </svg>
            </button>
            <div class="detail-chrome__title">
              <span class="project-pill"><b>ASS</b> Agent Software Studio</span>
              <strong>Next-gen chat conversation event projection</strong>
            </div>
            <button class="detail-chrome__edit" title="Rename task" aria-label="Rename task">
              <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                @for (path of iconPath('edit'); track path) {
                  <path [attr.d]="path"></path>
                }
              </svg>
            </button>
            <span class="detail-chrome__state">2-ready</span>
            <button class="detail-chrome__complete" title="Complete and move to next task">Complete & Next</button>
          </header>

          <section class="workbench" aria-label="Task chat workbench">
            <mockup-next-gen-chat-rail
              [paneButtons]="paneButtons"
              [scenarios]="scenarios"
              [activeScenario]="activeScenario()"
              [summaryChips]="summaryChips"
              [activePanes]="activePaneIds()"
              [openCount]="openDocuments().length"
              (guideRequested)="guideOpen.set(true)"
              (allDocumentsRequested)="openAllContextPanes()"
              (paneSelected)="togglePane($event)"
              (scenarioSelected)="activeScenario.set($event)"
              (summaryPaneSelected)="setPane($event)">
            </mockup-next-gen-chat-rail>

            <div class="workbench__main">
            <mockup-next-gen-chat-document-tabs
              [documents]="openDocuments()"
              [activeDocument]="activeDocument()"
              (documentActivated)="activateDocument($event)"
              (documentClosed)="closeDocument($event)">
            </mockup-next-gen-chat-document-tabs>
            <div class="workbench__body"
                 style="grid-template-columns:minmax(0, 1fr)"
                 [attr.data-chat-open]="chatOpen()"
                 [attr.data-context-open]="contextOpen()"
                 [attr.data-split-dragging]="splitDragging()">
              @if (activeDocument() === 'chat' && chatOpen()) {
              <section class="conversation" aria-label="Conversation" data-testid="prototype-conversation">
                <div class="conversation__topline">
                  <span class="badge badge--ok">Task Chat</span>
                  <span>{{ scenarioText() }}</span>
                  <button (click)="setPane('git')">
                    <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                      @for (path of iconPath('fileDiff'); track path) {
                        <path [attr.d]="path"></path>
                      }
                    </svg>
                    Changes
                  </button>
                  <button (click)="toggleChat()" data-testid="prototype-chat-close">
                    <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                      @for (path of iconPath('panelClose'); track path) {
                        <path [attr.d]="path"></path>
                      }
                    </svg>
                    Close chat
                  </button>
                  <button (click)="debugOpen.set(true)">
                    <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                      @for (path of iconPath('bug'); track path) {
                        <path [attr.d]="path"></path>
                      }
                    </svg>
                    Verbose Debug
                  </button>
                </div>

                <div class="conversation__scroll">
                <button class="run-marker"
                        type="button"
                        [class.run-marker--open]="markerOpen()"
                        (click)="markerOpen.set(!markerOpen())"
                        data-testid="prototype-run-marker">
                  <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                    @for (path of iconPath('clock'); track path) {
                      <path [attr.d]="path"></path>
                    }
                  </svg>
                  <span>Run 4 · 12m active · 28 tool calls · 42k tokens · 3 commits</span>
                  <b>{{ markerOpen() ? 'hide' : 'details' }}</b>
                </button>
                @if (markerOpen()) {
                  <aside class="run-popover" data-testid="prototype-run-popover">
                    <header>
                      <strong>Run 4 context</strong>
                      <span>One CLI invocation between user inputs</span>
                    </header>
                    <div class="run-popover__grid">
                      @for (item of runMarkerDetails; track item.label) {
                        <div>
                          <span>{{ item.label }}</span>
                          <b>{{ item.value }}</b>
                        </div>
                      }
                    </div>
                    <footer>
                      <button (click)="setPane('debug')">Debug this run</button>
                      <button (click)="setPane('git')">Review commits</button>
                      <button (click)="toolOpen.set(true)">Open tools</button>
                    </footer>
                  </aside>
                }

                <div class="actor-key" data-testid="prototype-actor-key" aria-label="Actors active in this run">
                  <span class="actor-key__label">Actors</span>
                  @for (kind of actorRailItems; track kind) {
                    <span class="actor-key__chip"
                          [attr.data-actor]="kind"
                          [attr.data-shape]="actorMeta(kind).shape"
                          [attr.title]="actorMeta(kind).help"
                          [attr.data-testid]="'prototype-actor-chip-' + kind">
                      <span class="actor-avatar"
                            [attr.data-shape]="actorMeta(kind).shape"
                            aria-hidden="true">
                        <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                          @for (path of iconPath(actorMeta(kind).icon); track path) {
                            <path [attr.d]="path"></path>
                          }
                        </svg>
                        <i>{{ actorMeta(kind).glyph }}</i>
                      </span>
                      <b>{{ actorMeta(kind).label }}</b>
                      <em>{{ actorRailCounts()[kind] }}</em>
                    </span>
                  }
                </div>

                @for (entry of visibleTurns(); track entry.id) {
                  @switch (entry.kind) {
                    @case ('turn') {
                      <article class="turn"
                               [attr.data-actor]="entry.actor"
                               [attr.data-shape]="actorMeta(entry.actor).shape"
                               [attr.data-testid]="'prototype-turn-' + entry.actor">
                        <span class="actor-avatar turn__avatar"
                              [attr.data-shape]="actorMeta(entry.actor).shape"
                              [attr.aria-label]="actorMeta(entry.actor).label">
                          <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                            @for (path of iconPath(actorMeta(entry.actor).icon); track path) {
                              <path [attr.d]="path"></path>
                            }
                          </svg>
                          <i>{{ actorMeta(entry.actor).glyph }}</i>
                        </span>
                        <div class="turn__body">
                          <header>
                            <strong>{{ entry.title }}</strong>
                            <span class="turn__role">{{ actorMeta(entry.actor).label }}</span>
                            @if (entry.intervention) {
                              <span class="turn__target"
                                    [attr.data-target]="entry.intervention"
                                    [attr.title]="interventionMeta(entry.intervention).help"
                                    [attr.data-testid]="'prototype-target-' + entry.intervention">
                                <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                                  @for (path of iconPath(interventionMeta(entry.intervention).icon); track path) {
                                    <path [attr.d]="path"></path>
                                  }
                                </svg>
                                <span>&rarr; {{ interventionMeta(entry.intervention).label }}</span>
                              </span>
                            }
                            @if (entry.meta) {
                              <em>{{ entry.meta }}</em>
                            }
                          </header>
                          <p>{{ entry.body }}</p>
                          @if (entry.actions?.length) {
                            <div class="turn__actions">
                              @for (action of entry.actions; track action) {
                                <button (click)="handleAction(action)">{{ action }}</button>
                              }
                            </div>
                          }
                        </div>
                      </article>
                    }
                    @case ('decision') {
                      <article class="decision"
                               [attr.data-actor]="entry.actor"
                               [attr.data-decision]="entry.decision"
                               [attr.data-tone]="entry.tone"
                               [attr.data-expanded]="isDecisionExpanded(entry.id)"
                               [attr.data-testid]="'prototype-decision-' + entry.decision">
                        <button class="decision__row"
                                type="button"
                                (click)="toggleDecision(entry.id)"
                                [attr.aria-expanded]="isDecisionExpanded(entry.id)"
                                [attr.aria-controls]="'decision-detail-' + entry.id">
                          <span class="actor-avatar"
                                [attr.data-shape]="actorMeta(entry.actor).shape"
                                aria-hidden="true">
                            <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                              @for (path of iconPath(decisionMeta[entry.decision].icon); track path) {
                                <path [attr.d]="path"></path>
                              }
                            </svg>
                            <i>{{ actorMeta(entry.actor).glyph }}</i>
                          </span>
                          <span class="decision__lead">
                            <strong>{{ decisionMeta[entry.decision].label }}</strong>
                            <span>{{ entry.title }}</span>
                          </span>
                          <span class="decision__summary">{{ entry.summary }}</span>
                          <span class="decision__retry">{{ entry.retry }}</span>
                          <b>{{ isDecisionExpanded(entry.id) ? 'Hide' : 'Details' }}</b>
                        </button>
                        @if (isDecisionExpanded(entry.id)) {
                          <div class="decision__detail"
                               [attr.id]="'decision-detail-' + entry.id"
                               [attr.data-testid]="'prototype-decision-detail-' + entry.decision">
                            <dl>
                              <div><dt>Reason</dt><dd>{{ entry.reason }}</dd></div>
                              <div><dt>Evidence</dt><dd>{{ entry.evidence }}</dd></div>
                              <div><dt>Action</dt><dd>{{ entry.action }}</dd></div>
                              <div><dt>Retry budget</dt><dd>{{ entry.retry }}</dd></div>
                              <div><dt>Token usage</dt><dd>{{ entry.tokens }}</dd></div>
                              <div><dt>Next step</dt><dd>{{ entry.nextStep }}</dd></div>
                            </dl>
                            <footer>
                              <button (click)="openTrace(entry.traceRange)">Open trace · {{ entry.traceRange }}</button>
                              <button (click)="setPane('debug')">Pin debug pane</button>
                              <button (click)="debugOpen.set(true)">Verbose Debug</button>
                            </footer>
                          </div>
                        }
                      </article>
                    }
                  }
                }

                <button class="tool-burst"
                        [class.tool-burst--open]="toolOpen()"
                        (click)="toolOpen.set(!toolOpen())"
                        data-testid="prototype-tool-burst">
                  <strong>Tools 28</strong>
                  <span>read 12 - search 7 - edit 4 - shell 3 - browser 2 - 1 failed - 4 artifacts</span>
                  <b>{{ toolOpen() ? 'close' : 'open' }}</b>
                </button>
                @if (toolOpen()) {
                  <div class="tool-details">
                    @for (row of toolRows; track row.tool) {
                      <div>
                        <code>{{ row.tool }}</code>
                        <span>{{ row.target }}</span>
                        <strong [attr.data-tone]="row.tone">{{ row.result }}</strong>
                      </div>
                    }
                  </div>
                }
                </div>

                <div class="composer" data-testid="prototype-composer">
                  <div class="composer__input">
                    <span>Reply in task context. Use #latest-run, #git, #screenshot, or /create-follow-up...</span>
                    <div class="composer__mentions">
                      <button (click)="setPane('git')">#git</button>
                      <button (click)="setPane('preview')">#screenshot</button>
                      <button (click)="setPane('debug')">#latest-run</button>
                      <button (click)="setPane('git')">#change-source</button>
                    </div>
                  </div>
                  <div class="composer__quick" aria-label="Chat mode">
                    @for (mode of composeModes; track mode.id) {
                      <button type="button"
                              [class.composer__mode--active]="composerMode() === mode.id"
                              [attr.title]="mode.description"
                              (click)="composerMode.set(mode.id)">
                        <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                          @for (path of iconPath(mode.icon); track path) {
                            <path [attr.d]="path"></path>
                          }
                        </svg>
                        <span>{{ mode.label }}</span>
                      </button>
                    }
                  </div>
                  <div class="composer__bar">
                    <div class="composer__context">
                      <button title="Attach files, screenshots, or report context" aria-label="Attach context">
                        <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                          @for (path of iconPath('plus'); track path) {
                            <path [attr.d]="path"></path>
                          }
                        </svg>
                        Attach
                      </button>
                      <button title="Scope follow-up to the active task">Task</button>
                      <button title="Permission mode for the next CLI run">Access</button>
                      <button title="Selected file context">ui.html</button>
                    </div>
                    <div class="composer__runtime">
                      <button title="CLI driver">Codex</button>
                      <button title="Model and reasoning">5.5 Extra High</button>
                      <button title="Start or continue the current task">
                        <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                          @for (path of iconPath('play'); track path) {
                            <path [attr.d]="path"></path>
                          }
                        </svg>
                        Start
                      </button>
                      <button title="Pause running agent">
                        <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                          @for (path of iconPath('pause'); track path) {
                            <path [attr.d]="path"></path>
                          }
                        </svg>
                        Pause
                      </button>
                      <button class="composer__send" title="Send follow-up">
                        <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                          @for (path of iconPath('play'); track path) {
                            <path [attr.d]="path"></path>
                          }
                        </svg>
                        {{ composerModeLabel() }}
                      </button>
                    </div>
                  </div>
                </div>
              </section>
              }

              @if (activeContextDocument(); as openPane) {
                <mockup-next-gen-chat-context-document
                  [pane]="openPane"
                  [chatOpen]="chatOpen()"
                  [featureParity]="featureParity"
                  [gitFiles]="gitFiles"
                  [activeGitFile]="activeGitFile()"
                  [screenshots]="screenshots"
                  [tokenRows]="tokenRows"
                  (toggleChatRequested)="toggleChat()"
                  (closePaneRequested)="closeContextPane($event)"
                  (debugRequested)="debugOpen.set(true)"
                  (paneSelected)="setPane($event)"
                  (featureSelected)="openFeatureParity($event)"
                  (activeGitFileChanged)="activeGitFile.set($event)"
                  (lightboxRequested)="lightboxOpen.set(true)">
                </mockup-next-gen-chat-context-document>
              }
            </div>
            </div>
          </section>
        </section>

        <aside class="sheet" aria-label="Project side sheet" data-testid="prototype-side-sheet">
          <header class="sheet__head">
            <div>
              <strong>Project side sheet chat</strong>
              <span>Project-level steering, queue context, task references</span>
            </div>
            <button class="icon-btn" (click)="sideSheetOpen.set(!sideSheetOpen())" title="Toggle side sheet" aria-label="Toggle side sheet">
              <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                @for (path of iconPath(sideSheetOpen() ? 'panelClose' : 'panelOpen'); track path) {
                  <path [attr.d]="path"></path>
                }
              </svg>
            </button>
          </header>
          <div class="sheet__body">
            <article>
              <strong>You</strong>
              <p>Keep the chat open while I inspect the result and Git changes next to it.</p>
            </article>
            <article>
              <strong>Orchestrator</strong>
              <p>The task chat owns evidence. The side sheet owns project steering, upcoming jobs, and cross-task decisions.</p>
            </article>
            <button class="sheet__summary" (click)="setPane('debug')">
              <strong>Queue signal</strong>
              <span>6 chat jobs aligned to v7 workbench · click to inspect debug context</span>
            </button>
          </div>
          <footer class="sheet__composer">
            <span>Ask in project side sheet...</span>
            <button>Send</button>
          </footer>
        </aside>
      </main>

      @if (statusPanel()) {
        <section class="status-popover modal__panel"
                 style="position:fixed;left:58px;right:10px;bottom:32px;z-index:19;width:auto;max-height:340px;display:grid;grid-template-columns:220px minmax(0,1fr) minmax(180px,.45fr);gap:10px;padding:10px;border-radius:8px 8px 0 0;"
                 [attr.data-panel]="statusPanel()"
                 data-testid="prototype-status-popover">
          <header style="display:grid;gap:6px;align-content:start;border-right:1px solid var(--line)">
            <strong>{{ statusPanelTitle() }}</strong>
            <button class="chip" (click)="statusPanel.set(null)">Close</button>
          </header>
          @switch (statusPanel()) {
            @case ('tokens') {
              <article>
                <h3>Token usage heat</h3>
                <div class="token-bars">
                  @for (row of tokenRows; track row.name) {
                    <div>
                      <span>{{ row.name }}</span>
                      <i><em [style.width.%]="row.percent"></em></i>
                      <b>{{ row.value }}</b>
                    </div>
                  }
                </div>
              </article>
              <article>
                <h3>Usage hover contract</h3>
                <p>Status bar usage keeps quota, token totals, refresh, timeline, and per-project/model drill-down in one compact hover surface.</p>
                <div class="function-grid">
                  <button>Refresh all CLIs</button>
                  <button>Open token timeline</button>
                  <button (click)="debugTab.set('tokens'); debugOpen.set(true)">Open token debug</button>
                  <button (click)="setPane('debug')">Pin token pane</button>
                </div>
              </article>
              <article>
                <h3>Quota strip</h3>
                <div class="usage-pop-grid">
                  @for (item of usageStrip; track item.label) {
                    <div [attr.data-tone]="item.tone">
                      <b>{{ item.value }}</b>
                      <span>{{ item.label }}</span>
                      @if (item.window) {
                        <em>{{ item.window }} window · resets {{ item.reset }}</em>
                      }
                    </div>
                  }
                </div>
              </article>
            }
            @case ('queue') {
              <article>
                <h3>Queue and automation</h3>
                <div class="metric-grid">
                  <div><b>2</b><span>running</span></div>
                  <div><b>4/6</b><span>auto loops</span></div>
                  <div><b>1</b><span>blocked</span></div>
                  <div><b>6</b><span>chat jobs</span></div>
                </div>
              </article>
              <article>
                <h3>Dummy actions</h3>
                <div class="function-grid">
                  <button (click)="setPane('result')">Open active task</button>
                  <button (click)="openQueueModule()">Open Queue module</button>
                  <button (click)="closeQueueModule()">Hide Queue module</button>
                  <button (click)="sideSheetOpen.set(true)">Open project sheet</button>
                  <button (click)="commandOpen.set(true)">Create follow-up</button>
                </div>
              </article>
            }
            @case ('health') {
              <article>
                <h3>System health</h3>
                <div class="metric-grid">
                  <div><b>Ready</b><span>runner</span></div>
                  <div><b>5031</b><span>stable API</span></div>
                  <div><b>12m</b><span>last run</span></div>
                  <div><b>0</b><span>fatal errors</span></div>
                </div>
              </article>
              <article>
                <h3>Observability shortcuts</h3>
                <p>Status items should become the fastest route into health, logs, quotas, visual evidence, and stuck-loop diagnostics.</p>
                <div class="function-grid">
                  <button (click)="debugTab.set('trace'); debugOpen.set(true)">Open trace</button>
                  <button (click)="toggleStatusPanel('queue')">Queue health</button>
                  <button (click)="toggleStatusPanel('tokens')">Token health</button>
                </div>
              </article>
            }
            @case ('evidence') {
              <article>
                <h3>Visual evidence</h3>
                <p>Four screenshots are attached to this job. The status bar keeps them one click away even while Git stays open beside chat.</p>
                <div class="function-grid">
                  <button (click)="setPane('preview')">Open preview</button>
                  <button (click)="lightboxOpen.set(true)">Open lightbox</button>
                  <button>Open evidence folder</button>
                </div>
              </article>
              <article style="display:grid;place-items:center;text-align:center;color:var(--muted);background:linear-gradient(135deg, rgba(122,167,255,.22), transparent 46%), var(--surface-soft)">
                <b>Latest</b>
                <span>Result split, light theme, 1440 x 900</span>
              </article>
            }
            @case ('session') {
              <article>
                <h3>Session continuity</h3>
                <div class="metric-grid">
                  <div><b>3</b><span>session chain</span></div>
                  <div><b>0</b><span>recoveries</span></div>
                  <div><b>run 4</b><span>active segment</span></div>
                  <div><b>ok</b><span>capture state</span></div>
                </div>
              </article>
              <article>
                <h3>Why it matters</h3>
                <p>The next version must show whether a continuation reused the vendor session, rebuilt context from disk, or lost capture. That state belongs in the status bar and verbose debug.</p>
                <div class="handoff-list">
                  <span><b>Resume key</b> codex:next-gen-chat:run-4</span>
                  <span><b>Context source</b> prompt.md + latest-run + status.md</span>
                  <span><b>Worklog</b> 12m visible, 9m billable CLI time</span>
                </div>
                <div class="function-grid">
                  <button (click)="markerOpen.set(true)">Open run marker</button>
                  <button (click)="debugTab.set('overview'); debugOpen.set(true)">Open session debug</button>
                </div>
              </article>
            }
            @case ('projects') {
              <article>
                <h3>Project filter and owner</h3>
                <div class="project-pop-list">
                  @for (project of projectTabs; track project.name) {
                    <button [class.project-pop-list__active]="project.active"
                            [style.--project-color]="project.color"
                            [style.--project-soft]="project.soft"
                            [style.--project-border]="project.border">
                      <span>{{ project.initial }}</span>
                      <b>{{ project.name }}</b>
                      <em>{{ project.auto }}</em>
                    </button>
                  }
                </div>
              </article>
              <article>
                <h3>Owner switch</h3>
                <p>The header keeps owner filtering first-class. Found-next should preserve the fast switch for Robert, Orchestrator, QA, and Unassigned without pushing task content down.</p>
                <div class="handoff-list">
                  <span><b>Scope</b> active project set plus owner filter</span>
                  <span><b>Default route</b> last selected project, then user-owned queue</span>
                  <span><b>Empty state</b> keep side sheet and status bar available</span>
                </div>
                <div class="function-grid">
                  <button class="function-grid__active">Robert</button>
                  <button>Orchestrator</button>
                  <button>QA</button>
                  <button>Unassigned</button>
                </div>
              </article>
            }
            @case ('model') {
              <article>
                <h3>CLI and model</h3>
                <div class="function-grid">
                  <button class="function-grid__active">Codex</button>
                  <button>Claude</button>
                  <button>Gemini</button>
                  <button>Copilot</button>
                </div>
              </article>
              <article>
                <h3>Run configuration</h3>
                <div class="function-grid">
                  <button class="function-grid__active">5.5 Extra High</button>
                  <button>5.4 High</button>
                  <button>Auto approve</button>
                  <button>Stop current run</button>
                </div>
              </article>
            }
          }
        </section>
      }

      <app-found-next-statusbar
        (statusPanelRequested)="toggleStatusPanel($event)"
        (sideSheetRequested)="sideSheetOpen.set(true)"
        (debugTraceRequested)="debugTab.set('trace'); debugOpen.set(true)">
      </app-found-next-statusbar>

      @if (debugOpen()) {
        <div class="modal" data-testid="prototype-debug-modal" (click)="debugOpen.set(false)">
          <section class="modal__panel modal__panel--debug" (click)="$event.stopPropagation()">
            <header>
              <strong>Verbose Debug</strong>
              <div>
                @for (tab of debugTabs; track tab.id) {
                  <button [class.modal__tab--active]="debugTab() === tab.id"
                          [attr.data-testid]="'prototype-debug-tab-' + tab.id"
                          (click)="debugTab.set(tab.id)">
                    {{ tab.label }}
                  </button>
                }
                <button (click)="debugOpen.set(false)">Close</button>
              </div>
            </header>
            <div class="debug-grid">
              @switch (debugTab()) {
                @case ('actors') {
                  <article>
                    <h3>Actor counts</h3>
                    <div class="metric-grid">
                      <div><b>7</b><span>agent turns</span></div>
                      <div><b>2</b><span>orchestrator</span></div>
                      <div><b>1</b><span>supervisor</span></div>
                      <div><b>1</b><span>QA report</span></div>
                    </div>
                  </article>
                  <article>
                    <h3>Decision trail</h3>
                    <p>Orchestrator reissued once after a fast heuristic done. Supervisor observed one quiet window and did not kill the run.</p>
                  </article>
                }
                @case ('tools') {
                  <article>
                    <h3>Tool calls</h3>
                    @for (row of toolRows; track row.tool) {
                      <div class="debug-band">
                        <span>{{ row.tool }} · {{ row.target }}</span>
                        <i><em [style.width.%]="row.tone === 'danger' ? 42 : 72"></em></i>
                        <b>{{ row.result }}</b>
                      </div>
                    }
                  </article>
                  <article>
                    <h3>Raw trace range</h3>
                    <p>Lines 418-731 collapse into the visible tool burst. Expanding shows command, duration, exit code, and artifact links.</p>
                  </article>
                }
                @case ('tokens') {
                  <article>
                    <h3>Token pressure</h3>
                    <div class="token-bars">
                      @for (row of tokenRows; track row.name) {
                        <div>
                          <span>{{ row.name }}</span>
                          <i><em [style.width.%]="row.percent"></em></i>
                          <b>{{ row.value }}</b>
                        </div>
                      }
                    </div>
                  </article>
                  <article>
                    <h3>Budget interpretation</h3>
                    <p>Token heat should be visible at task, run, project, and supporting-agent level. The default chat shows only pressure and trend.</p>
                  </article>
                }
                @case ('trace') {
                  <article>
                    <h3>Trace filters</h3>
                    <div class="function-grid">
                      <button>actor:agent</button>
                      <button>actor:orchestrator</button>
                      <button>tool:failed</button>
                      <button>artifact:image</button>
                      <button>sentinel:matched</button>
                      <button>schema:drift</button>
                    </div>
                  </article>
                  <article>
                    <h3>Raw source</h3>
                    <p>The raw Activity Log remains one click away. The chat projection is a readable lens, not a replacement for evidence.</p>
                  </article>
                }
                @default {
                  <article>
                    <h3>Overview</h3>
                    <div class="metric-grid">
                      <div><b>12m</b><span>duration</span></div>
                      <div><b>28</b><span>tool calls</span></div>
                      <div><b>42k</b><span>tokens</span></div>
                      <div><b>3</b><span>commits</span></div>
                    </div>
                  </article>
                  <article>
                    <h3>Timeline</h3>
                    @for (row of debugBands; track row.name) {
                      <div class="debug-band">
                        <span>{{ row.name }}</span>
                        <i><em [style.width.%]="row.percent"></em></i>
                        <b>{{ row.value }}</b>
                      </div>
                    }
                  </article>
                  <article>
                    <h3>Explanation</h3>
                    <p>The workbench keeps normal chat readable while preserving the deep diagnostic surface for confusing runs. It is read-only and links back to raw trace, tokens, commits, screenshots, and task markers.</p>
                  </article>
                }
              }
            </div>
          </section>
        </div>
      }

      @if (guideOpen()) {
        <div class="modal" data-testid="prototype-rail-guide-modal" (click)="guideOpen.set(false)">
          <section class="modal__panel modal__panel--guide" (click)="$event.stopPropagation()">
            <header>
              <strong>Workbench rail</strong>
              <button (click)="guideOpen.set(false)">Close</button>
            </header>
            <div class="guide-grid">
              <article>
                <h3>Container rule</h3>
                <p>Activity Bar modules navigate. Side Bar views select. Workbench documents do the work. Status Bar items summarize.</p>
              </article>
              <article>
                <h3>Queue is optional</h3>
                <p>The Queue is the Tasks module. It can open by default, close when review needs width, and return from the Activity Bar.</p>
              </article>
              <article>
                <h3>Document area</h3>
                <p>Summary, Task Chat, Git changes, Screenshots, and Debug trace behave like opened documents, not permanent dashboard cards.</p>
              </article>
              <article>
                <h3>Actions stay scoped</h3>
                <p>Pane headers keep contextual actions sparse. Rare choices move to status popovers, command palette, or Verbose Debug.</p>
              </article>
            </div>
          </section>
        </div>
      }

      @if (lightboxOpen()) {
        <div class="modal" data-testid="prototype-lightbox" (click)="lightboxOpen.set(false)">
          <section class="modal__panel modal__panel--image" (click)="$event.stopPropagation()">
            <header>
              <strong>Screenshot evidence</strong>
              <button (click)="lightboxOpen.set(false)">Close</button>
            </header>
            <div class="image-box">
              <strong>Workbench Git split, verified</strong>
              <code>docs/mockups/chat-window-next-gen/evidence/angular-prototype-git.png</code>
            </div>
          </section>
        </div>
      }

      @if (featureModal(); as feature) {
        <div class="modal" data-testid="prototype-feature-modal" (click)="featureModal.set(null)">
          <section class="modal__panel modal__panel--guide" (click)="$event.stopPropagation()">
            <header>
              <strong>{{ featureTitle(feature) }}</strong>
              <button (click)="featureModal.set(null)">Close</button>
            </header>
            <div class="guide-grid">
              @switch (feature) {
                @case ('prompt') {
                  <article>
                    <h3>Prompt history</h3>
                    <p><code>prompt.md</code> is the original task. <code>prompt-2.md</code> and <code>prompt-3.md</code> are user extensions that should remain reviewable without leaving the chat workbench.</p>
                  </article>
                  <article>
                    <h3>Edit path</h3>
                    <p>Production should keep preview, source edit, save state, and prompt-history breadcrumbs. This mock keeps the interaction as a quick modal.</p>
                    <div class="function-grid">
                      <button>Preview prompt.md</button>
                      <button>Edit prompt.md</button>
                      <button>Open history diff</button>
                    </div>
                  </article>
                }
                @case ('timeline') {
                  <article>
                    <h3>Run timeline</h3>
                    <div class="metric-grid">
                      <div><b>Run 1</b><span>start</span></div>
                      <div><b>Run 2</b><span>continue</span></div>
                      <div><b>Run 3</b><span>reissue</span></div>
                      <div><b>Run 4</b><span>review</span></div>
                    </div>
                  </article>
                  <article>
                    <h3>Expected behavior</h3>
                    <p>The default transcript gets a tiny run marker. Full history opens here or in Verbose Debug, including session id, trace range, commits, and token pressure.</p>
                    <div class="function-grid">
                      <button (click)="markerOpen.set(true); featureModal.set(null)">Open run marker</button>
                      <button (click)="debugTab.set('overview'); debugOpen.set(true); featureModal.set(null)">Verbose timeline</button>
                    </div>
                  </article>
                }
                @case ('startStop') {
                  <article>
                    <h3>Run controls</h3>
                    <div class="metric-grid">
                      <div><b>Codex</b><span>driver</span></div>
                      <div><b>5.5</b><span>model</span></div>
                      <div><b>Full</b><span>access</span></div>
                      <div><b>Paused</b><span>safe stop ready</span></div>
                    </div>
                  </article>
                  <article>
                    <h3>Composer deck</h3>
                    <p>Start, pause, stop, access mode, model, CLI, and selected file context stay in the bottom composer area. The task header only carries durable task state.</p>
                    <div class="function-grid">
                      <button (click)="composerMode.set('continue'); featureModal.set(null)">Focus continue</button>
                      <button (click)="toggleStatusPanel('model'); featureModal.set(null)">Model settings</button>
                      <button>Stop run</button>
                    </div>
                  </article>
                }
              }
            </div>
          </section>
        </div>
      }

      @if (commandOpen()) {
        <div class="modal" data-testid="prototype-command-palette" (click)="commandOpen.set(false)">
          <section class="command" (click)="$event.stopPropagation()">
            <input value="workbench: switch to git changes" aria-label="Command palette input" />
            <button (click)="setPane('git'); commandOpen.set(false)">Pin Git review</button>
            <button (click)="setPane('preview'); commandOpen.set(false)">Open screenshots</button>
            <button (click)="setPane('debug'); commandOpen.set(false)">Open debug pane</button>
            <button (click)="markerOpen.set(true); commandOpen.set(false)">Inspect current run</button>
            <button (click)="sideSheetOpen.set(!sideSheetOpen()); commandOpen.set(false)">Toggle project side sheet</button>
            <button (click)="composerMode.set('steer'); commandOpen.set(false)">Switch composer to steering</button>
          </section>
        </div>
      }
      </section>
    } @else {
      <section class="ng-chat-prototype ng-chat-prototype--closed"
               [attr.data-theme]="theme()"
               data-testid="next-gen-chat-angular-prototype">
        <div class="prototype-closed" data-testid="prototype-closed-state">
          <strong>Prototype closed</strong>
          <span>Restart the mockup server or refresh the page to open it again.</span>
        </div>
      </section>
    }
  `
})
export class NextGenChatWorkbenchPrototypeComponent {
  readonly closed = signal(false);
  readonly pane = signal<ContextPane>('result');
  readonly density = signal<Density>('comfortable');
  readonly theme = signal<Theme>('light');
  readonly activeScenario = signal<Scenario>('review');
  readonly toolOpen = signal(false);
  readonly sideSheetOpen = signal(true);
  readonly debugOpen = signal(false);
  readonly lightboxOpen = signal(false);
  readonly commandOpen = signal(false);
  readonly guideOpen = signal(false);
  readonly markerOpen = signal(false);
  readonly featureModal = signal<FeatureAction | null>(null);
  readonly statusPanel = signal<StatusPanel | null>(null);
  readonly activeActivity = signal<ActivityTarget>('tasks');
  readonly queueOpen = signal(true);
  readonly chatOpen = signal(true);
  readonly contextPanes = signal<readonly ContextPane[]>(['result']);
  readonly contextOpen = computed(() => this.contextPanes().length > 0);
  readonly activeDocument = signal<WorkbenchDocumentId>('result');
  readonly activeContextDocument = computed<ContextPane | null>(() => {
    const active = this.activeDocument();
    if (active === 'chat') return null;
    return active;
  });
  readonly activePaneIds = computed<readonly WorkbenchPane[]>(() => [
    ...(this.chatOpen() ? (['chat'] as const) : []),
    ...this.contextPanes(),
  ]);
  readonly splitRatio = signal(54);
  readonly splitDragging = signal(false);
  readonly activeGitFile = signal('frontend/src/mockups/next-gen-chat/app/next-gen-chat-workbench-prototype.component.ts');
  readonly debugTab = signal<DebugTab>('overview');
  readonly composerMode = signal<ComposeMode>('continue');

  readonly projectTabs = PROJECT_TABS;
  readonly usageStrip = USAGE_STRIP;
  readonly iconPaths = ICON_PATHS;

  readonly activityItems: ActivityItem[] = [
    { id: 'projects', icon: 'folder', label: 'Projects', title: 'Projects and watched paths' },
    { id: 'tasks', icon: 'columns', label: 'Tasks', title: 'Task board and queue' },
    { id: 'search', icon: 'search', label: 'Search', title: 'Search chat and trace' },
    { id: 'git', icon: 'git', label: 'Git', title: 'Git changes' },
    { id: 'qa', icon: 'check', label: 'QA', title: 'QA, tests, and health' },
    { id: 'tokens', icon: 'tokens', label: 'Tokens', title: 'Token usage' },
  ];

  readonly paneButtons: PaneButton[] = [
    { id: 'chat', label: 'Open chat document', short: 'Chat', icon: 'chat' },
    { id: 'result', label: 'Open summary document', short: 'Summary', icon: 'check' },
    { id: 'git', label: 'Open Git document', short: 'Git', icon: 'git' },
    { id: 'preview', label: 'Open screenshot document', short: 'Preview', icon: 'image' },
    { id: 'debug', label: 'Open debug document', short: 'Debug', icon: 'bug' },
  ];

  readonly scenarios: ScenarioOption[] = [
    { id: 'review', label: 'Review', icon: 'check' },
    { id: 'tools', label: 'Tools', icon: 'terminal' },
    { id: 'wait', label: 'Wait', icon: 'clock' },
    { id: 'visual', label: 'Images', icon: 'image' },
    { id: 'drift', label: 'Drift', icon: 'warning' },
    { id: 'decisions', label: 'Decisions', icon: 'shield' },
  ];

  readonly actors: Record<ActorKind, ActorMeta> = {
    user: { kind: 'user', label: 'You', glyph: 'Y', icon: 'user', shape: 'pill', help: 'Human steering. Always right-aligned and target-tagged.' },
    agent: { kind: 'agent', label: 'Task Agent', glyph: 'A', icon: 'agent', shape: 'circle', help: 'The CLI working the active task.' },
    orchestrator: { kind: 'orchestrator', label: 'Orchestrator', glyph: 'O', icon: 'compass', shape: 'hex', help: 'Deterministic post-run policy. Reissues, heuristics, retry budget.' },
    supervisor: { kind: 'supervisor', label: 'Supervisor', glyph: 'S', icon: 'shield', shape: 'shield', help: 'Watchdog and circuit breaker. Quiet, resume, kill.' },
    support: { kind: 'support', label: 'Supporting Agent', glyph: 'Q', icon: 'helper', shape: 'rounded', help: 'Sub-agent or QA helper feeding a structured report back to the task.' },
    tool: { kind: 'tool', label: 'Tool Runner', glyph: 'T', icon: 'terminal', shape: 'square', help: 'Read, search, edit, shell, browser, and test invocations.' },
    system: { kind: 'system', label: 'System', glyph: '!', icon: 'warning', shape: 'triangle', help: 'Parser, capture, and contract warnings from the orchestrator runtime.' },
  };

  readonly actorRailItems: ActorKind[] = ['user', 'agent', 'orchestrator', 'supervisor', 'support', 'tool', 'system'];

  readonly interventionTargets: Record<InterventionTarget, { label: string; help: string; icon: string }> = {
    currentRun: { label: 'Current run', icon: 'play', help: 'Steers the run that is active right now.' },
    nextRun: { label: 'Next run', icon: 'clock', help: 'Lands as continuation context for the next CLI invocation.' },
    orchestrator: { label: 'Orchestrator', icon: 'compass', help: 'Talks to the deterministic post-run policy, not the agent body.' },
    followUp: { label: 'Follow-up task', icon: 'plus', help: 'Spawns a queued follow-up task instead of changing this run.' },
  };

  readonly decisionMeta: Record<DecisionKind, { label: string; actor: ActorKind; tone: 'info' | 'warn' | 'danger'; icon: string }> = {
    reissue: { label: 'Reissue', actor: 'orchestrator', tone: 'info', icon: 'rerun' },
    heuristic: { label: 'Heuristic outcome', actor: 'orchestrator', tone: 'warn', icon: 'warning' },
    needsInput: { label: 'Needs-input loop', actor: 'orchestrator', tone: 'warn', icon: 'help' },
    circuit: { label: 'Circuit breaker', actor: 'supervisor', tone: 'danger', icon: 'shield' },
    captureFail: { label: 'Capture fail', actor: 'system', tone: 'warn', icon: 'plug' },
    drift: { label: 'Schema drift', actor: 'system', tone: 'warn', icon: 'warning' },
  };

  readonly composeModes: Array<{ id: ComposeMode; label: string; icon: string; description: string }> = [
    { id: 'continue', label: 'Continue', icon: 'play', description: 'Continue the running task or restart the next run with this follow-up.' },
    { id: 'extend', label: 'Extend task', icon: 'file', description: 'Append a task extension to prompt history before the next run.' },
    { id: 'steer', label: 'Steer', icon: 'panel', description: 'Send steering to the orchestrator without changing the task body.' },
    { id: 'followup', label: 'Follow-up job', icon: 'plus', description: 'Create a queued follow-up task from this chat turn.' },
  ];

  readonly debugTabs: Array<{ id: DebugTab; label: string }> = [
    { id: 'overview', label: 'Overview' },
    { id: 'actors', label: 'Actors' },
    { id: 'tools', label: 'Tools' },
    { id: 'tokens', label: 'Tokens' },
    { id: 'trace', label: 'Trace' },
  ];

  readonly summaryChips: SummaryChip[] = [
    { value: 'Review', label: 'run 4', icon: 'check', pane: 'result', tone: 'ok' },
    { value: '42k', label: 'tokens', icon: 'tokens', pane: 'debug', tone: 'warn' },
    { value: '3', label: 'commits', icon: 'git', pane: 'git' },
    { value: '8', label: 'files', icon: 'fileDiff', pane: 'git' },
    { value: '4', label: 'images', icon: 'image', pane: 'preview' },
    { value: '1', label: 'retry fail', icon: 'warning', pane: 'debug', tone: 'danger' },
    { value: '12m', label: 'active', icon: 'clock', pane: 'result' },
  ];

  readonly taskCards: TaskQueueCard[] = [
    { id: 'bridge', title: 'Chat layout integration bridge', state: 'ready', lane: '2-ready', order: '50', agent: 'Codex', meta: '12m est', active: false },
    { id: 'projection', title: 'Next-gen chat conversation event projection', state: 'ready', lane: '2-ready', order: '60', agent: 'Codex', meta: 'active ref', active: true },
    { id: 'tools', title: 'Collapse tool-heavy chat logs into bursts', state: 'ready', lane: '2-ready', order: '70', agent: 'Claude', meta: 'QA linked', active: false },
    { id: 'actors', title: 'Chat actor rails and decision cards', state: 'ready', lane: '2-ready', order: '80', agent: 'Codex', meta: 'needs spec', active: false },
    { id: 'debug', title: 'Fullscreen verbose debug view', state: 'ready', lane: '2-ready', order: '90', agent: 'Codex', meta: 'debug view', active: false },
  ];

  readonly runMarkerDetails = [
    { label: 'CLI', value: 'Codex' },
    { label: 'Model', value: '5.5 Extra High' },
    { label: 'Mode', value: 'Continue' },
    { label: 'Session', value: 'preserved' },
    { label: 'Trace', value: 'lines 418-731' },
    { label: 'Outcome', value: 'review' },
    { label: 'Budget', value: '42k tokens' },
    { label: 'Artifacts', value: '4 screenshots' },
  ];

  readonly featureParity: FeatureParityItem[] = [
    { label: 'Prompt history', icon: 'file', note: 'Original prompt plus task extensions remain visible.', action: 'prompt' },
    { label: 'Activity and Trace', icon: 'terminal', note: 'Raw CLI output stays available through the debug lens.', action: 'activity' },
    { label: 'Run timeline', icon: 'clock', note: 'Run cards become thin chat markers with popover metadata.', action: 'timeline' },
    { label: 'Git review', icon: 'git', note: 'Files, commits, diff, commit message, and commit action remain accessible.', action: 'git' },
    { label: 'Screenshots', icon: 'image', note: 'Durable screenshot evidence opens in the preview pane.', action: 'screenshots' },
    { label: 'Token usage', icon: 'tokens', note: 'Task and project token pressure stay visible without crowding the transcript.', action: 'tokens' },
    { label: 'Side sheet', icon: 'panelOpen', note: 'Project-level steering remains in the resizable side sheet.', action: 'sideSheet' },
    { label: 'Start/Stop', icon: 'play', note: 'Execution controls move into the compact composer command deck.', action: 'startStop' },
  ];

  readonly transcript: TranscriptEntry[] = [
    {
      kind: 'turn',
      id: 'user-steer',
      actor: 'user',
      title: 'You',
      body: 'I want chat to be optional. Sometimes I need the transcript, sometimes I need Git, result, screenshots, or debug panes to own the workspace.',
      intervention: 'orchestrator',
    },
    {
      kind: 'turn',
      id: 'agent-1',
      actor: 'agent',
      title: 'Task Agent',
      body: 'The workbench treats Chat, Result, Git, Preview, and Debug as pinable panes. They can be combined without turning the task view into a full docking system.',
      actions: ['Show technical layer', 'Open Verbose Debug'],
    },
    {
      kind: 'decision',
      id: 'reissue-1',
      decision: 'reissue',
      actor: 'orchestrator',
      title: 'Orchestrator reissued the run',
      summary: 'Fast Done after follow-up. Reissued once with stronger framing.',
      tone: 'info',
      reason: 'Agent emitted [[TASK_DONE]] within 18s of a UserContinue carrying a follow-up. Policy treats this as suspect.',
      evidence: 'cli-output.log lines 412-431 plus prior follow-up at line 318.',
      action: 'Reissue with stronger framing once, then stop and ask the user.',
      retry: 'used 1 of 1 reissues',
      tokens: '+3.2k orchestrator tokens',
      traceRange: 'lines 318-431',
      nextStep: 'If next run also returns fast Done, the policy escalates to human review.',
    },
    {
      kind: 'turn',
      id: 'agent-2',
      actor: 'agent',
      title: 'Task Agent',
      body: 'Re-running with the stronger frame. The reissue turned a fast Done into a real implementation pass with three commits and four screenshots.',
      meta: 'run 4 active',
    },
    {
      kind: 'turn',
      id: 'support-1',
      actor: 'support',
      title: 'Design QA',
      meta: 'helper agent',
      body: 'Light mode is primary, dark mode matches hierarchy, mobile collapses to chat, and click interception is covered by Playwright.',
      actions: ['Open screenshots'],
    },
    {
      kind: 'turn',
      id: 'tool-1',
      actor: 'tool',
      title: 'Tool Runner',
      meta: '28 calls',
      body: 'read 12, search 7, edit 4, shell 3, browser 2. One shell failure on playwright chromium retried successfully.',
      actions: ['Show technical layer'],
    },
  ];

  readonly waitDecision: DecisionEntry = {
    kind: 'decision',
    id: 'circuit-1',
    decision: 'circuit',
    actor: 'supervisor',
    title: 'Supervisor watched a quiet window',
    summary: '30s silent, agent resumed. No kill issued.',
    tone: 'warn',
    reason: 'Agent stdout went quiet at line 612 and resumed at line 614 without producing structured output.',
    evidence: 'last output was a tool spawn header; resume produced an answer 30s later.',
    action: 'Hold the kill switch. Emit advisory only.',
    retry: 'within 1/3 quiet windows for this run',
    tokens: 'no orchestrator tokens spent',
    traceRange: 'lines 612-679',
    nextStep: 'A second quiet window above 90s would trip the circuit breaker.',
  };

  readonly driftDecision: DecisionEntry = {
    kind: 'decision',
    id: 'drift-1',
    decision: 'drift',
    actor: 'system',
    title: 'Schema drift in structured report',
    summary: 'Report does not match the JSON contract. Markdown body still renders.',
    tone: 'warn',
    reason: 'Expected `{ summary, evidence, nextStep }` but the agent emitted free-form Markdown headings.',
    evidence: 'parser warning at line 731. Raw Markdown remains attached.',
    action: 'Surface as a system row. Keep the Markdown human-readable. Flag drift in metrics.',
    retry: 'no retry consumed',
    tokens: 'no extra orchestrator tokens',
    traceRange: 'lines 715-742',
    nextStep: 'If drift recurs in the next run, queue a contract follow-up task.',
  };

  readonly decisionShowcase: TranscriptEntry[] = [
    {
      kind: 'turn',
      id: 'user-currentRun',
      actor: 'user',
      title: 'You',
      body: 'Stop the active run. The Playwright shell call is in a retry loop and will burn tokens.',
      intervention: 'currentRun',
    },
    {
      kind: 'decision',
      id: 'heuristic-1',
      decision: 'heuristic',
      actor: 'orchestrator',
      title: 'Outcome inferred without a sentinel',
      summary: 'Could not classify the agent reply. Fell back to heuristic.',
      tone: 'warn',
      reason: 'No hard sentinel matched. Last 60 lines suggest "needs review", confidence 0.52.',
      evidence: 'matched phrase "ready for human review" at line 504; no [[TASK_*]] sentinel in the log.',
      action: 'Mark MatchedSentinel = false. Surface heuristic verdict as a meta message.',
      retry: 'retry budget unchanged',
      tokens: '+0.4k parser tokens',
      traceRange: 'lines 446-505',
      nextStep: 'Recommend the agent emit [[TASK_DONE]] explicitly on the next pass.',
    },
    {
      kind: 'decision',
      id: 'needsInput-1',
      decision: 'needsInput',
      actor: 'orchestrator',
      title: 'Needs-input loop counter advanced',
      summary: 'Third needs-input in a row. One slot left before circuit trip.',
      tone: 'warn',
      reason: 'Agent asked the same disambiguation three times. Loop guard threshold is 4.',
      evidence: 'sentinels [[TASK_NEEDS_INPUT:scope]] at lines 220, 318, 401.',
      action: 'Answer with the most recent project rule, mark loop counter at 3/4.',
      retry: 'loop 3 of 4',
      tokens: '+1.1k orchestrator tokens',
      traceRange: 'lines 220-401',
      nextStep: 'A fourth identical question hands off to the user.',
    },
    {
      kind: 'turn',
      id: 'user-followUp',
      actor: 'user',
      title: 'You',
      body: 'Do not change this task body. Queue a follow-up that fixes the Playwright shell flake.',
      intervention: 'followUp',
    },
    {
      kind: 'decision',
      id: 'circuit-showcase',
      decision: 'circuit',
      actor: 'supervisor',
      title: 'Supervisor armed the circuit breaker',
      summary: 'Two quiet windows in this run. Next breach trips kill.',
      tone: 'danger',
      reason: 'Quiet windows of 30s and 65s back-to-back without structured output.',
      evidence: 'watchdog markers at lines 612 and 740. No tool calls in between.',
      action: 'Hold kill. Raise the breaker to "armed". Next quiet > 90s ends the run.',
      retry: '2 of 3 quiet windows used',
      tokens: 'no orchestrator tokens spent',
      traceRange: 'lines 612-742',
      nextStep: 'Operator can pre-empt with Pause to keep tokens out of a kill cycle.',
    },
    {
      kind: 'decision',
      id: 'captureFail-1',
      decision: 'captureFail',
      actor: 'system',
      title: 'Session capture failed',
      summary: 'No Claude session id from this run. Next continuation rebuilds from disk.',
      tone: 'warn',
      reason: 'CLI exited before the session sentinel landed in `~/.claude/projects/.../session.jsonl`.',
      evidence: '[capture-fail] log marker at line 802. Session registry sees no id.',
      action: 'Mark session as rebuilt-on-next-continue. Keep the run output intact.',
      retry: 'no retry consumed',
      tokens: 'no orchestrator tokens spent',
      traceRange: 'lines 798-815',
      nextStep: 'Next continuation re-derives prompt history and attaches the original task body.',
    },
    {
      kind: 'turn',
      id: 'user-nextRun',
      actor: 'user',
      title: 'You',
      body: 'Before you start the next run, switch the model to Haiku 4.5 to keep the budget tight.',
      intervention: 'nextRun',
    },
    {
      kind: 'decision',
      id: 'drift-showcase',
      decision: 'drift',
      actor: 'system',
      title: 'Schema drift in structured report',
      summary: 'Report does not match the JSON contract. Markdown body still renders.',
      tone: 'warn',
      reason: 'Expected `{ summary, evidence, nextStep }` but the agent emitted free-form Markdown headings.',
      evidence: 'parser warning at line 731. Raw Markdown remains attached.',
      action: 'Surface as a system row. Keep the Markdown human-readable. Flag drift in metrics.',
      retry: 'no retry consumed',
      tokens: 'no extra orchestrator tokens',
      traceRange: 'lines 715-742',
      nextStep: 'If drift recurs in the next run, queue a contract follow-up task.',
    },
  ];

  readonly toolRows = [
    { tool: 'read', target: 'activity-log.parser.ts', result: 'ok', tone: 'ok' },
    { tool: 'search', target: '136 cli-output.log fixtures', result: 'ok', tone: 'ok' },
    { tool: 'shell', target: 'playwright chromium', result: 'failed once', tone: 'danger' },
    { tool: 'browser', target: 'v7 workbench screenshots', result: 'passed', tone: 'ok' },
  ];

  readonly gitFiles: GitFileRow[] = [
    { path: 'frontend/src/mockups/next-gen-chat/app/next-gen-chat-workbench-prototype.component.ts', delta: '+812 -0' },
    { path: 'frontend/src/mockups/next-gen-chat/app/next-gen-chat-context-document.component.ts', delta: '+214 -0' },
    { path: 'frontend/src/app/app.ts', delta: '+3 -0' },
    { path: 'frontend/src/app/services/feature-flags.service.ts', delta: '+8 -0' },
    { path: 'docs/mockups/chat-window-next-gen/README.md', delta: '+9 -1' },
    { path: 'frontend/e2e/next-gen-chat-angular-prototype.spec.ts', delta: '+82 -0' },
  ];

  readonly screenshots = ['Result split', 'Git split', 'Compact mode', 'Debug modal'];

  readonly tokenRows: TokenUsageRow[] = [
    { name: 'Agent', value: '28k', percent: 82 },
    { name: 'Orch.', value: '9k', percent: 34 },
    { name: 'Support', value: '5k', percent: 18 },
  ];

  readonly debugBands = [
    { name: 'Tool density', value: '28 calls', percent: 78 },
    { name: 'Tokens', value: '42k', percent: 64 },
    { name: 'Wait loop', value: '30s', percent: 38 },
    { name: 'Images', value: '4 files', percent: 52 },
  ];

  readonly visibleTurns = computed<TranscriptEntry[]>(() => {
    const scenario = this.activeScenario();
    if (scenario === 'tools') return this.transcript;
    if (scenario === 'wait') {
      return [...this.transcript, this.waitDecision];
    }
    if (scenario === 'visual') {
      return this.transcript.map((entry) =>
        entry.kind === 'turn' && entry.actor === 'support'
          ? { ...entry, body: 'Screenshots are rendered as a compact evidence reel and open into a durable lightbox. Scratch output is never the only evidence path.' }
          : entry
      );
    }
    if (scenario === 'drift') {
      return [...this.transcript, this.driftDecision];
    }
    if (scenario === 'decisions') {
      return [...this.transcript, ...this.decisionShowcase];
    }
    return this.transcript;
  });

  readonly scenarioText = computed(() => {
    switch (this.activeScenario()) {
      case 'tools':
        return 'Tool-heavy logs collapse into one readable row by default. Expand for exact commands and raw trace ranges.';
      case 'wait':
        return 'Watchdog quiet, resume, and kill events become low-noise supervisor rows with timing detail.';
      case 'visual':
        return 'Visual evidence gets a preview pane and lightbox without turning the transcript into a gallery.';
      case 'drift':
        return 'Parser drift, duplicate sentinels, and malformed reports stay visible but human-first.';
      case 'decisions':
        return 'Reissue, heuristic, needs-input, circuit, capture-fail, and drift become one-line rows with full causal detail on expand.';
      default:
        return 'Review mode lets chat, result, Git, screenshots, and debug panes be pinned only when useful.';
    }
  });

  readonly actorRailCounts = computed<Record<ActorKind, number>>(() => {
    const counts: Record<ActorKind, number> = {
      user: 0, agent: 0, orchestrator: 0, supervisor: 0, support: 0, tool: 0, system: 0,
    };
    for (const entry of this.visibleTurns()) {
      counts[entry.actor] += 1;
    }
    return counts;
  });

  readonly expandedDecisions = signal<ReadonlySet<string>>(new Set());

  isDecisionExpanded(id: string): boolean {
    return this.expandedDecisions().has(id);
  }

  toggleDecision(id: string): void {
    const next = new Set(this.expandedDecisions());
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    this.expandedDecisions.set(next);
  }

  actorMeta(kind: ActorKind): ActorMeta {
    return this.actors[kind];
  }

  interventionMeta(target: InterventionTarget) {
    return this.interventionTargets[target];
  }

  readonly openDocuments = computed<WorkbenchDocument[]>(() => {
    const docs: WorkbenchDocument[] = [];
    if (this.contextPanes().includes('result')) {
      docs.push({ id: 'result', title: 'Summary', subtitle: 'default dashboard', icon: 'check', closable: false });
    }
    if (this.chatOpen()) {
      docs.push({ id: 'chat', title: 'Task Chat', subtitle: 'conversation', icon: 'chat', closable: true });
    }
    for (const pane of this.contextPanes()) {
      if (pane === 'result') continue;
      docs.push({
        id: pane,
        title: this.documentTitle(pane),
        subtitle: this.documentSubtitle(pane),
        icon: this.documentIcon(pane),
        closable: true,
      });
    }
    if (docs.length === 0) {
      docs.push({ id: 'result', title: 'Summary', subtitle: 'default dashboard', icon: 'check', closable: false });
    }
    return docs;
  });

  readonly workbenchColumns = computed(() => {
    const columns: string[] = [];
    const panes = this.contextPanes();
    const manyPanes = panes.length > 1;
    if (this.chatOpen()) {
      const chatMinimum = manyPanes ? 300 : 320;
      const chatShare = manyPanes ? Math.min(this.splitRatio(), 48) : this.splitRatio();
      columns.push(`minmax(${chatMinimum}px, ${chatShare}%)`);
      if (panes.length > 0) columns.push('7px');
    }
    for (const pane of panes) {
      if (manyPanes) {
        columns.push(pane === 'git' ? 'minmax(360px, 1.25fr)' : 'minmax(220px, .85fr)');
      } else {
        columns.push(pane === 'git' ? 'minmax(430px, 1.25fr)' : 'minmax(255px, .85fr)');
      }
    }
    return columns.length ? columns.join(' ') : 'minmax(0, 1fr)';
  });

  readonly composerModeLabel = computed(() => {
    switch (this.composerMode()) {
      case 'extend': return 'Extend';
      case 'steer': return 'Steer';
      case 'followup': return 'Create job';
      default: return 'Continue';
    }
  });

  setPane(pane: WorkbenchPane): void {
    if (pane === 'chat') {
      this.chatOpen.set(true);
      this.activeDocument.set('chat');
      return;
    }
    this.pane.set(pane);
    this.activeDocument.set(pane);
    this.addContextPane(pane);
  }

  isPaneButtonActive(pane: WorkbenchPane): boolean {
    if (pane === 'chat') return this.chatOpen();
    return this.contextPanes().includes(pane);
  }

  togglePane(pane: WorkbenchPane): void {
    this.setPane(pane);
  }

  toggleChat(): void {
    if (this.chatOpen() && this.contextPanes().length === 0) {
      this.addContextPane('result');
    }
    const wasActiveChat = this.activeDocument() === 'chat';
    const nextOpen = !this.chatOpen();
    this.chatOpen.set(nextOpen);
    if (nextOpen) {
      this.activeDocument.set('chat');
    } else if (wasActiveChat) {
      this.activeDocument.set(this.contextPanes()[0] ?? 'result');
    }
  }

  closeContextPane(pane: ContextPane): void {
    this.removeContextPane(pane);
  }

  openAllContextPanes(): void {
    this.contextPanes.set(['result', 'git', 'preview', 'debug']);
    this.pane.set('result');
    this.activeDocument.set('result');
    this.sideSheetOpen.set(false);
  }

  activateDocument(id: WorkbenchDocumentId): void {
    this.setPane(id);
  }

  closeDocument(id: WorkbenchDocumentId): void {
    if (id === 'chat') {
      const wasActiveChat = this.activeDocument() === 'chat';
      this.chatOpen.set(false);
      if (wasActiveChat) {
        this.activeDocument.set(this.contextPanes()[0] ?? 'result');
      }
      return;
    }
    if (id === 'result') return;
    this.removeContextPane(id);
  }

  documentTitle(pane: WorkbenchPane): string {
    switch (pane) {
      case 'chat': return 'Task Chat';
      case 'git': return 'Git changes';
      case 'preview': return 'Screenshots';
      case 'debug': return 'Debug trace';
      default: return 'Summary';
    }
  }

  documentSubtitle(pane: WorkbenchPane): string {
    switch (pane) {
      case 'chat': return 'conversation';
      case 'git': return 'source diff';
      case 'preview': return 'visual evidence';
      case 'debug': return 'diagnostics';
      default: return 'default dashboard';
    }
  }

  documentIcon(pane: WorkbenchPane): string {
    switch (pane) {
      case 'chat': return 'chat';
      case 'git': return 'git';
      case 'preview': return 'image';
      case 'debug': return 'bug';
      default: return 'check';
    }
  }

  private addContextPane(pane: ContextPane): void {
    this.contextPanes.update((panes) => {
      if (panes.includes(pane)) return panes;
      const next = [...panes, pane];
      if (next.length > 1) this.sideSheetOpen.set(false);
      return next;
    });
  }

  private removeContextPane(pane: ContextPane): void {
    this.contextPanes.update((panes) => panes.filter((openPane) => openPane !== pane));
    if (this.activeDocument() === pane) {
      this.activeDocument.set(this.chatOpen() ? 'chat' : (this.contextPanes()[0] ?? 'result'));
    }
    if (!this.chatOpen() && this.contextPanes().length === 0) {
      this.chatOpen.set(true);
      this.activeDocument.set('chat');
    }
  }

  setSplitRatioValue(value: number): void {
    this.splitRatio.set(Math.max(34, Math.min(72, Math.round(value))));
  }

  startSplitResize(event: PointerEvent): void {
    if (!this.chatOpen() || !this.contextOpen()) return;

    const host = (event.currentTarget as HTMLElement).parentElement;
    if (!host) return;

    event.preventDefault();
    this.splitDragging.set(true);

    const updateFromPointer = (rawEvent: Event) => {
      const pointer = rawEvent as PointerEvent;
      const rect = host.getBoundingClientRect();
      if (rect.width <= 0) return;
      const next = ((pointer.clientX - rect.left) / rect.width) * 100;
      this.setSplitRatioValue(next);
    };

    const stopResize = () => {
      this.splitDragging.set(false);
      document.removeEventListener('pointermove', updateFromPointer);
    };

    updateFromPointer(event);
    document.addEventListener('pointermove', updateFromPointer);
    document.addEventListener('pointerup', stopResize, { once: true });
  }

  resizeSplitFromKeyboard(event: KeyboardEvent): void {
    const step = event.shiftKey ? 10 : 4;
    let next: number | null = null;

    if (event.key === 'ArrowLeft') next = this.splitRatio() - step;
    if (event.key === 'ArrowRight') next = this.splitRatio() + step;
    if (event.key === 'Home') next = 34;
    if (event.key === 'End') next = 72;

    if (next === null) return;
    event.preventDefault();
    this.setSplitRatioValue(next);
  }

  handleActivity(target: ActivityTarget): void {
    this.activeActivity.set(target);
    if (target === 'git') this.setPane('git');
    if (target === 'qa') this.toggleStatusPanel('health');
    if (target === 'tokens') this.toggleStatusPanel('tokens');
    if (target === 'tasks') this.toggleQueueModule();
    if (target === 'search') this.commandOpen.set(true);
    if (target === 'projects') this.sideSheetOpen.set(true);
  }

  openQueueModule(): void {
    this.queueOpen.set(true);
    this.activeActivity.set('tasks');
  }

  closeQueueModule(): void {
    this.queueOpen.set(false);
    if (this.activeActivity() === 'tasks') {
      this.activeActivity.set('projects');
    }
  }

  toggleQueueModule(): void {
    if (this.queueOpen()) this.closeQueueModule();
    else this.openQueueModule();
  }

  openFeatureParity(action: FeatureAction): void {
    switch (action) {
      case 'activity':
        this.debugTab.set('trace');
        this.debugOpen.set(true);
        return;
      case 'timeline':
        this.featureModal.set('timeline');
        return;
      case 'git':
        this.setPane('git');
        return;
      case 'screenshots':
        this.setPane('preview');
        return;
      case 'tokens':
        this.debugTab.set('tokens');
        this.debugOpen.set(true);
        return;
      case 'sideSheet':
        this.sideSheetOpen.set(true);
        return;
      case 'startStop':
        this.featureModal.set('startStop');
        return;
      default:
        this.featureModal.set('prompt');
    }
  }

  featureTitle(feature: FeatureAction): string {
    switch (feature) {
      case 'timeline': return 'Run timeline';
      case 'startStop': return 'Start and stop controls';
      default: return 'Prompt history';
    }
  }

  toggleStatusPanel(panel: StatusPanel): void {
    this.statusPanel.set(this.statusPanel() === panel ? null : panel);
  }

  statusPanelTitle(): string {
    switch (this.statusPanel()) {
      case 'queue': return 'Queue and automation';
      case 'tokens': return 'CLI usage and tokens';
      case 'evidence': return 'Visual evidence';
      case 'session': return 'Session continuity';
      case 'projects': return 'Project and owner filters';
      case 'model': return 'CLI and model controls';
      default: return 'System health';
    }
  }

  toggleDensity(): void {
    this.density.set(this.density() === 'compact' ? 'comfortable' : 'compact');
  }

  toggleTheme(): void {
    this.theme.set(this.theme() === 'light' ? 'dark' : 'light');
  }

  handleAction(action: string): void {
    if (action === 'Open Verbose Debug' || action === 'Debug pane') this.debugOpen.set(true);
    if (action === 'Git split') this.setPane('git');
    if (action === 'Open screenshots') this.setPane('preview');
    if (action === 'Open changes') this.setPane('git');
    if (action === 'Show technical layer') this.toolOpen.set(true);
  }

  openTrace(_range: string): void {
    this.debugTab.set('trace');
    this.debugOpen.set(true);
  }

  iconPath(name: string): string[] {
    return this.iconPaths[name] ?? this.iconPaths['panel'];
  }

  close(): void {
    this.closed.set(true);
  }
}

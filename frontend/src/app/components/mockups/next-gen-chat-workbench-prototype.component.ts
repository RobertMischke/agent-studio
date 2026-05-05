import { Component, computed, signal } from '@angular/core';

type WorkbenchPane = 'result' | 'git' | 'preview' | 'debug' | 'chat';
type ContextPane = Exclude<WorkbenchPane, 'chat'>;
type Density = 'comfortable' | 'compact';
type Theme = 'light' | 'dark';
type Scenario = 'review' | 'tools' | 'wait' | 'visual' | 'drift' | 'decisions';
type DebugTab = 'overview' | 'actors' | 'tools' | 'tokens' | 'trace';
type ComposeMode = 'continue' | 'extend' | 'steer' | 'followup';
type ActivityTarget = 'projects' | 'tasks' | 'search' | 'git' | 'qa' | 'tokens';
type StatusPanel = 'health' | 'queue' | 'tokens' | 'evidence' | 'model';
type ActorKind = 'user' | 'agent' | 'orchestrator' | 'supervisor' | 'support' | 'tool' | 'system';
type InterventionTarget = 'currentRun' | 'nextRun' | 'orchestrator' | 'followUp';
type DecisionKind = 'reissue' | 'heuristic' | 'needsInput' | 'circuit' | 'captureFail' | 'drift';
type FeatureAction = 'prompt' | 'activity' | 'timeline' | 'git' | 'screenshots' | 'tokens' | 'sideSheet' | 'startStop';

interface SummaryChip {
  label: string;
  value: string;
  icon: string;
  pane: WorkbenchPane;
  tone?: 'ok' | 'warn' | 'danger';
}

interface ActorMeta {
  kind: ActorKind;
  label: string;
  glyph: string;
  icon: string;
  shape: 'circle' | 'rounded' | 'square' | 'hex' | 'shield' | 'triangle' | 'pill';
  help: string;
}

interface ChatTurnEntry {
  kind: 'turn';
  id: string;
  actor: ActorKind;
  title: string;
  body: string;
  meta?: string;
  actions?: string[];
  intervention?: InterventionTarget;
}

interface DecisionEntry {
  kind: 'decision';
  id: string;
  decision: DecisionKind;
  actor: ActorKind;
  title: string;
  summary: string;
  tone: 'info' | 'warn' | 'danger';
  reason: string;
  evidence: string;
  action: string;
  retry: string;
  tokens: string;
  traceRange: string;
  nextStep: string;
}

type TranscriptEntry = ChatTurnEntry | DecisionEntry;

@Component({
  selector: 'app-next-gen-chat-workbench-prototype',
  standalone: true,
  template: `
    @if (!closed()) {
      <section class="ng-chat-prototype"
               [attr.data-theme]="theme()"
               [attr.data-density]="density()"
               [attr.data-pane]="pane()"
               data-testid="next-gen-chat-angular-prototype">
      <nav class="activity" aria-label="Prototype activity bar" data-testid="prototype-activity-bar">
        @for (item of activityItems; track item.id) {
          <button class="activity__item"
                  [class.activity__item--active]="activeActivity() === item.id"
                  [attr.title]="item.title"
                  [attr.aria-label]="item.title"
                  [attr.data-testid]="'prototype-activity-' + item.id"
                  (click)="handleActivity(item.id)">
            <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
              @for (path of iconPath(item.icon); track path) {
                <path [attr.d]="path"></path>
              }
            </svg>
          </button>
        }
        <span class="activity__spacer"></span>
        <button class="activity__item" title="Close prototype" aria-label="Close prototype" (click)="close()">
          <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
            @for (path of iconPath('close'); track path) {
              <path [attr.d]="path"></path>
            }
          </svg>
        </button>
      </nav>

      <header class="topbar">
        <div class="topbar__title">
          <strong>Agent Task Processor</strong>
          <span>Task workbench</span>
        </div>
        <div class="topbar__runline" aria-label="Current run summary" data-testid="prototype-topbar-runline">
          <span>Run 4</span>
          <span>12m</span>
          <span>28 tools</span>
          <span>42k tokens</span>
          <span>3 commits</span>
        </div>
        <div class="topbar__actions">
          <button class="icon-btn"
                  [class.icon-btn--active]="sideSheetOpen()"
                  title="Toggle project sheet"
                  aria-label="Toggle project sheet"
                  (click)="sideSheetOpen.set(!sideSheetOpen())"
                  data-testid="prototype-topbar-sheet">
            <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
              @for (path of iconPath(sideSheetOpen() ? 'panelClose' : 'panelOpen'); track path) {
                <path [attr.d]="path"></path>
              }
            </svg>
          </button>
          <button class="icon-btn"
                  [class.icon-btn--active]="statusPanel() === 'queue'"
                  title="Queue and automation"
                  aria-label="Queue and automation"
                  (click)="toggleStatusPanel('queue')"
                  data-testid="prototype-topbar-queue">
            <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
              @for (path of iconPath('columns'); track path) {
                <path [attr.d]="path"></path>
              }
            </svg>
          </button>
          <button class="icon-btn" title="Toggle density" aria-label="Toggle density" (click)="toggleDensity()" data-testid="prototype-density-toggle">
            <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
              @for (path of iconPath(density() === 'compact' ? 'expand' : 'compress'); track path) {
                <path [attr.d]="path"></path>
              }
            </svg>
          </button>
          <button class="icon-btn" title="Toggle theme" aria-label="Toggle theme" (click)="toggleTheme()" data-testid="prototype-theme-toggle">
            <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
              @for (path of iconPath(theme() === 'light' ? 'sun' : 'moon'); track path) {
                <path [attr.d]="path"></path>
              }
            </svg>
          </button>
          <button class="icon-btn" title="Command palette" aria-label="Command palette" (click)="commandOpen.set(true)" data-testid="prototype-command-open">
            <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
              @for (path of iconPath('command'); track path) {
                <path [attr.d]="path"></path>
              }
            </svg>
          </button>
          <button class="icon-btn" title="Verbose debug" aria-label="Verbose debug" (click)="debugOpen.set(true)" data-testid="prototype-debug-open">
            <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
              @for (path of iconPath('bug'); track path) {
                <path [attr.d]="path"></path>
              }
            </svg>
          </button>
          <button class="icon-btn" title="Close prototype" aria-label="Close prototype" (click)="close()">
            <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
              @for (path of iconPath('close'); track path) {
                <path [attr.d]="path"></path>
              }
            </svg>
          </button>
        </div>
      </header>

      <main class="workspace" [class.workspace--sheet-closed]="!sideSheetOpen()">
        <aside class="task-list" aria-label="Task list">
          <div class="task-list__header">
            <strong>Queue</strong>
            <span>2-ready · 5 tasks</span>
          </div>
          @for (task of taskCards; track task.id) {
            <button class="task-card"
                    [class.task-card--active]="task.active"
                    [attr.data-state]="task.state">
              <span class="task-card__title">{{ task.title }}</span>
              <span class="task-card__meta">{{ task.meta }}</span>
            </button>
          }
        </aside>

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
              <span class="project-pill"><b>ATP</b> Agent Task Processor</span>
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
            <aside class="inspector-rail" aria-label="Task inspector rail">
              <button class="rail-guide" type="button" title="Explain this rail" (click)="guideOpen.set(true)" data-testid="prototype-rail-guide">
                <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                  @for (path of iconPath('panel'); track path) {
                    <path [attr.d]="path"></path>
                  }
                </svg>
                <span>Views</span>
                <small>pins + run facts</small>
              </button>
              <div class="inspector-rail__modes" data-testid="prototype-layout-buttons">
                <b>Panes</b>
                <button class="rail-action rail-action--all"
                        title="Pin all review panes"
                        aria-label="Pin all review panes"
                        data-testid="prototype-pane-all"
                        (click)="openAllContextPanes()">
                  <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                    @for (path of iconPath('columns'); track path) {
                      <path [attr.d]="path"></path>
                    }
                  </svg>
                  <span>All</span>
                  <em>{{ contextPanes().length + (chatOpen() ? 1 : 0) }}</em>
                </button>
                @for (mode of paneButtons; track mode.id) {
                  <button class="rail-action"
                          [class.icon-btn--active]="isPaneButtonActive(mode.id)"
                          [attr.title]="mode.label"
                          [attr.data-testid]="'prototype-pane-' + mode.id"
                          (click)="togglePane(mode.id)">
                    <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                      @for (path of iconPath(mode.icon); track path) {
                        <path [attr.d]="path"></path>
                      }
                    </svg>
                    <span>{{ mode.short }}</span>
                  </button>
                }
              </div>
              <div class="inspector-rail__scenarios" data-testid="prototype-scenarios">
                <b>Cases</b>
                @for (scenario of scenarios; track scenario.id) {
                  <button [class.scenario-row__active]="activeScenario() === scenario.id"
                          [attr.title]="scenario.label"
                          [attr.aria-label]="scenario.label"
                          [attr.data-testid]="'prototype-scenario-' + scenario.id"
                          (click)="activeScenario.set(scenario.id)">
                    <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                      @for (path of iconPath(scenario.icon); track path) {
                        <path [attr.d]="path"></path>
                      }
                    </svg>
                    <span>{{ scenario.label }}</span>
                  </button>
                }
              </div>
              <div class="inspector-rail__summary" data-testid="prototype-summary-strip">
                <b>Signals</b>
                @for (chip of summaryChips; track chip.label) {
                  <button class="summary-chip"
                          [attr.data-tone]="chip.tone || 'neutral'"
                          [attr.title]="chip.value + ' ' + chip.label"
                          (click)="setPane(chip.pane)">
                    <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                      @for (path of iconPath(chip.icon); track path) {
                        <path [attr.d]="path"></path>
                      }
                    </svg>
                    <strong>{{ chip.value }}</strong>
                    <span>{{ chip.label }}</span>
                  </button>
                }
              </div>
            </aside>

            <div class="workbench__main">
            <div class="workbench__body"
                 [style.gridTemplateColumns]="workbenchColumns()"
                 [attr.data-chat-open]="chatOpen()"
                 [attr.data-context-open]="contextOpen()"
                 [attr.data-split-dragging]="splitDragging()">
              @if (chatOpen()) {
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

              @if (chatOpen() && contextPanes().length > 0) {
                <div class="workbench-splitter"
                     role="separator"
                     tabindex="0"
                     aria-orientation="vertical"
                     aria-label="Resize chat and review panes"
                     aria-valuemin="34"
                     aria-valuemax="72"
                     [attr.aria-valuenow]="splitRatio()"
                     (pointerdown)="startSplitResize($event)"
                     (keydown)="resizeSplitFromKeyboard($event)"
                     data-testid="prototype-splitter">
                  <span aria-hidden="true"></span>
                </div>
              }

              @for (openPane of contextPanes(); track openPane) {
                <aside class="context"
                       aria-label="Workbench context pane"
                       [attr.data-pane]="openPane"
                       [attr.data-testid]="'prototype-pane-' + openPane + '-view'">
                  <header class="context__head">
                    <div>
                      <strong>{{ paneTitle(openPane) }}</strong>
                      <span>{{ paneSubtitle(openPane) }}</span>
                    </div>
                    <div>
                      <button class="icon-btn"
                              (click)="toggleChat()"
                              [title]="chatOpen() ? 'Close chat' : 'Open chat'"
                              [attr.aria-label]="chatOpen() ? 'Close chat' : 'Open chat'"
                              data-testid="prototype-chat-toggle">
                        <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                          @for (path of iconPath(chatOpen() ? 'panelClose' : 'panelOpen'); track path) {
                            <path [attr.d]="path"></path>
                          }
                        </svg>
                      </button>
                      <button class="icon-btn"
                              (click)="closeContextPane(openPane)"
                              [title]="'Close ' + paneTitle(openPane)"
                              [attr.aria-label]="'Close ' + paneTitle(openPane)"
                              [attr.data-testid]="'prototype-pane-' + openPane + '-close'">
                        <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                          @for (path of iconPath('close'); track path) {
                            <path [attr.d]="path"></path>
                          }
                        </svg>
                      </button>
                      <button class="icon-btn" (click)="debugOpen.set(true)" title="Open Verbose Debug" aria-label="Open Verbose Debug">
                        <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                          @for (path of iconPath('bug'); track path) {
                            <path [attr.d]="path"></path>
                          }
                        </svg>
                      </button>
                    </div>
                  </header>
                  @switch (openPane) {
                    @case ('result') {
                      <section class="context__body">
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
                            @for (item of featureParity; track item.label) {
                              <button [attr.title]="item.note" (click)="openFeatureParity(item.action)">
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
                            @for (file of gitFiles; track file.path) {
                              <button class="file-row"
                                      [class.task-card--active]="activeGitFile() === file.path"
                                      (click)="activeGitFile.set(file.path)">
                                <code>{{ file.path }}</code>
                                <span>{{ file.delta }}</span>
                              </button>
                            }
                            <div class="git-actions">
                              <button (click)="setSplitRatioValue(64)">Wider chat</button>
                              <button (click)="setSplitRatioValue(42)">Wider editor</button>
                              <button (click)="toggleChat()">{{ chatOpen() ? 'Hide chat' : 'Show chat' }}</button>
                            </div>
                          </div>
                          <article class="source-card" data-testid="prototype-git-editor">
                            <strong>Source editor / diff</strong>
                            <span>{{ activeGitFile() }}</span>
                            <code>{{ selectedGitFile().delta }} · staged preview</code>
                            <pre style="white-space:pre-wrap;font:12px/1.45 ui-monospace,SFMono-Regular,Consolas,monospace;margin:10px 0 0;color:var(--text)">@@ next-gen chat workbench
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
                          @for (shot of screenshots; track shot) {
                            <button (click)="lightboxOpen.set(true)">
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
                          @for (row of tokenRows; track row.name) {
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
                 style="position:fixed;left:58px;right:10px;bottom:26px;z-index:19;width:auto;max-height:280px;display:grid;grid-template-columns:220px minmax(0,1fr) minmax(180px,.45fr);gap:10px;padding:10px;border-radius:8px 8px 0 0;"
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
                <h3>Drill-down path</h3>
                <p>Job tokens, supporting-agent tokens, and orchestrator tokens stay visible in the bar. Clicking opens run-level heat and the Verbose Debug token tab.</p>
                <div class="function-grid">
                  <button (click)="debugTab.set('tokens'); debugOpen.set(true)">Open token debug</button>
                  <button (click)="setPane('debug')">Pin token pane</button>
                  <button>Export token report</button>
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

      <footer class="statusbar" data-testid="prototype-statusbar">
        <div class="statusbar__group">
          <button (click)="toggleStatusPanel('health')" data-testid="prototype-status-health">
            <span style="width:7px;height:7px;border-radius:999px;background:#90ee90"></span>
            <span>2 running</span>
          </button>
          <button (click)="toggleStatusPanel('queue')" data-testid="prototype-status-queue">
            <span>4/6 auto</span>
          </button>
          <button (click)="setPane('result')">
            <span>main</span>
          </button>
        </div>
        <div class="statusbar__group statusbar__group--center">
          <button (click)="toggleStatusPanel('tokens')" data-testid="prototype-status-token">
            <span>42k tokens</span>
          </button>
          <button (click)="setPane('git')" data-testid="prototype-status-git">
            <span>3 commits</span>
          </button>
          <button (click)="toggleStatusPanel('evidence')" data-testid="prototype-status-evidence">
            <span>4 screenshots</span>
          </button>
          <button (click)="setPane('debug')">
            <span>28 tools</span>
          </button>
        </div>
        <div class="statusbar__group statusbar__group--right">
          <button (click)="toggleStatusPanel('model')" data-testid="prototype-status-model">
            <span>Codex · 5.5 Extra High</span>
          </button>
          <button (click)="toggleDensity()">
            <span>{{ density() }}</span>
          </button>
          <button (click)="toggleTheme()">
            <span>{{ theme() }}</span>
          </button>
          <button (click)="commandOpen.set(true)">
            <span>Command</span>
          </button>
        </div>
      </footer>

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
                <h3>Why it is here</h3>
                <p>The rail keeps controls out of the transcript, so chat and Git or Result can use the full height.</p>
              </article>
              <article>
                <h3>What it contains</h3>
                <p>Task tabs, run signals, split presets, and scenario cases. Each item has a tooltip and visible label in comfortable density.</p>
              </article>
              <article>
                <h3>How it should ship</h3>
                <p>The production version should bind these controls to real task tabs, token data, commits, screenshots, and ConversationEvent projections.</p>
              </article>
              <article>
                <h3>What not to lose</h3>
                <p>Start, Stop, model choice, permissions, prompt history, Activity, Trace, Files, Commits, Screenshots, quota, and project side-sheet steering all remain reachable from this workbench.</p>
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
  `,
  styles: [`
    :host {
      position: fixed;
      inset: 0;
      z-index: 5000;
      display: block;
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }

    * { box-sizing: border-box; }
    button { font: inherit; cursor: pointer; }

    .svg-icon {
      width: 16px;
      height: 16px;
      display: block;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.8;
      stroke-linecap: round;
      stroke-linejoin: round;
      flex: 0 0 auto;
    }

    .ng-chat-prototype {
      --bg: #f3f5f8;
      --chrome: #f8fafc;
      --surface: #ffffff;
      --surface-soft: #eef2f7;
      --line: #d7dfeb;
      --line-strong: #b9c5d6;
      --text: #172033;
      --muted: #667287;
      --faint: #93a0b4;
      --accent: #356ad8;
      --ok: #328a3b;
      --warn: #c56b00;
      --danger: #d21f2b;
      --purple: #7457d9;
      --teal: #157e80;
      --shadow-soft: rgba(15, 23, 42, 0.10);
      --primary: var(--text);
      --primary-text: var(--surface);
      --scrollbar-thumb: #aeb9ca;
      height: 100vh;
      display: grid;
      grid-template-columns: 48px minmax(0, 1fr);
      grid-template-rows: 32px minmax(0, 1fr) 22px;
      color: var(--text);
      background: var(--bg);
      overflow: hidden;
      letter-spacing: 0;
    }

    .ng-chat-prototype[data-theme="dark"] {
      --bg: #1f2026;
      --chrome: #24262f;
      --surface: #2b2e38;
      --surface-soft: #343844;
      --line: #4b5160;
      --line-strong: #687184;
      --text: #f4f0e8;
      --muted: #c7c0b3;
      --faint: #9ea39b;
      --accent: #8fb8ff;
      --ok: #a6d189;
      --warn: #e6b673;
      --danger: #f27d8a;
      --purple: #c5a4ff;
      --teal: #8bd5ca;
      --shadow-soft: rgba(0, 0, 0, 0.28);
      --primary: #8fb8ff;
      --primary-text: #11141a;
      --scrollbar-thumb: #626a7a;
    }

    .ng-chat-prototype--closed {
      grid-template-columns: 1fr;
      grid-template-rows: 1fr;
      place-items: center;
    }

    .inspector-rail,
    .conversation__scroll,
    .context__body,
    .sheet__body,
    .modal__panel,
    .command {
      scrollbar-width: thin;
      scrollbar-color: var(--scrollbar-thumb) transparent;
      scrollbar-gutter: stable;
    }

    .inspector-rail::-webkit-scrollbar,
    .conversation__scroll::-webkit-scrollbar,
    .context__body::-webkit-scrollbar,
    .sheet__body::-webkit-scrollbar,
    .modal__panel::-webkit-scrollbar,
    .command::-webkit-scrollbar {
      width: 9px;
      height: 9px;
    }

    .inspector-rail::-webkit-scrollbar-thumb,
    .conversation__scroll::-webkit-scrollbar-thumb,
    .context__body::-webkit-scrollbar-thumb,
    .sheet__body::-webkit-scrollbar-thumb,
    .modal__panel::-webkit-scrollbar-thumb,
    .command::-webkit-scrollbar-thumb {
      border: 2px solid transparent;
      border-radius: 999px;
      background: var(--scrollbar-thumb);
      background-clip: padding-box;
    }

    .prototype-closed {
      display: grid;
      gap: 8px;
      max-width: 360px;
      padding: 18px 20px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
      box-shadow: 0 12px 36px rgba(15, 23, 42, 0.12);
      text-align: center;
    }

    .prototype-closed strong {
      font-size: 15px;
    }

    .prototype-closed span {
      color: var(--muted);
      font-size: 13px;
      line-height: 1.4;
    }

    .activity {
      grid-row: 1 / 4;
      display: grid;
      grid-template-rows: repeat(6, 44px) minmax(0, 1fr) 44px;
      justify-items: center;
      align-items: center;
      padding-top: 6px;
      background: color-mix(in srgb, var(--chrome) 68%, var(--bg));
      border-right: 1px solid var(--line);
    }

    .activity__item,
    .icon-btn {
      width: 28px;
      height: 28px;
      border: 1px solid transparent;
      border-radius: 6px;
      display: grid;
      place-items: center;
      background: transparent;
      color: var(--muted);
      font-size: 12px;
      font-weight: 700;
    }

    .activity__item .svg-icon,
    .icon-btn .svg-icon {
      width: 15px;
      height: 15px;
    }

    .activity__item:hover,
    .activity__item--active,
    .icon-btn:hover,
    .icon-btn--active {
      color: var(--text);
      background: var(--surface);
      border-color: var(--line);
    }

    .activity__spacer { min-height: 0; }

    .topbar {
      grid-column: 2;
      display: grid;
      grid-template-columns: minmax(160px, 1fr) auto auto;
      align-items: center;
      gap: 10px;
      padding: 0 10px 0 12px;
      background: var(--chrome);
      border-bottom: 1px solid var(--line);
    }

    .topbar__title {
      min-width: 0;
      display: flex;
      align-items: center;
      gap: 10px;
      font-size: 12px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .topbar__title span { color: var(--muted); }

    .topbar__runline {
      min-width: 0;
      display: flex;
      align-items: center;
      justify-content: flex-end;
      gap: 5px;
      overflow: hidden;
    }

    .topbar__runline span {
      min-height: 20px;
      display: inline-flex;
      align-items: center;
      border: 1px solid var(--line);
      border-radius: 999px;
      padding: 1px 7px;
      background: var(--surface);
      color: var(--muted);
      font-size: 11px;
      font-weight: 650;
      white-space: nowrap;
    }

    .topbar__actions { display: flex; gap: 5px; }

    .workspace {
      grid-column: 2;
      min-width: 0;
      min-height: 0;
      display: grid;
      grid-template-columns: 156px minmax(620px, 1fr) minmax(304px, 29vw);
      background: var(--bg);
    }

    .workspace--sheet-closed {
      grid-template-columns: 156px minmax(620px, 1fr) 0;
    }

    .task-list,
    .sheet {
      min-width: 0;
      min-height: 0;
      overflow: auto;
      background: var(--surface);
      border-right: 1px solid var(--line);
    }

    .task-list__header {
      display: grid;
      gap: 2px;
      padding: 10px;
      border-bottom: 1px solid var(--line);
      background: var(--chrome);
    }

    .task-list__header span,
    .task-card__meta,
    .context__head span,
    .sheet__head span {
      color: var(--muted);
      font-size: 11px;
    }

    .task-card {
      width: calc(100% - 12px);
      margin: 6px;
      display: grid;
      gap: 3px;
      text-align: left;
      padding: 8px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
      color: var(--text);
    }

    .task-card--active {
      border-color: var(--accent);
      box-shadow: inset 3px 0 0 var(--accent);
    }

    .task-card__title {
      font-weight: 650;
      font-size: 12px;
      line-height: 1.25;
      display: -webkit-box;
      -webkit-line-clamp: 2;
      -webkit-box-orient: vertical;
      overflow: hidden;
    }

    .detail {
      min-width: 0;
      min-height: 0;
      display: grid;
      grid-template-rows: 38px minmax(0, 1fr);
      border-right: 1px solid var(--line);
    }

    .detail-chrome {
      min-width: 0;
      min-height: 38px;
      display: grid;
      grid-template-columns: 30px minmax(0, 1fr) 28px auto auto;
      align-items: center;
      gap: 6px;
      padding: 4px 8px;
      border-bottom: 1px solid var(--line);
      background: var(--surface);
    }

    .detail-chrome__back,
    .detail-chrome__edit {
      width: 28px;
      height: 28px;
      border: 1px solid var(--line);
      border-radius: 6px;
      display: grid;
      place-items: center;
      background: var(--surface-soft);
      color: var(--muted);
    }

    .detail-chrome__title {
      min-width: 0;
      display: grid;
      gap: 1px;
    }

    .detail-chrome__title strong {
      min-width: 0;
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
      font-size: 13px;
    }

    .project-pill {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      color: var(--muted);
      font-size: 10px;
    }

    .project-pill b {
      padding: 2px 5px;
      border-radius: 999px;
      background: var(--accent);
      color: #fff;
      font-size: 9px;
    }

    .detail-chrome__state,
    .detail-chrome__complete {
      min-height: 26px;
      border: 1px solid var(--line);
      border-radius: 999px;
      padding: 3px 9px;
      color: var(--ok);
      background: var(--surface-soft);
      font-size: 11px;
      font-weight: 750;
      white-space: nowrap;
    }

    .detail-chrome__complete {
      border-radius: 6px;
      color: var(--text);
      background: var(--surface-soft);
      border-color: var(--line);
    }

    .composer__bar > div,
    .context__head > div:last-child {
      display: flex;
      align-items: center;
      gap: 5px;
    }

    .chip,
    .badge,
    .summary-chip,
    .scenario-row button,
    .turn__actions button,
    .composer button,
    .sheet__composer button,
    .modal button {
      border: 1px solid var(--line);
      border-radius: 7px;
      background: var(--surface);
      color: var(--text);
      min-height: 26px;
      padding: 3px 8px;
      font-size: 12px;
    }

    .summary-chip {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      border-radius: 999px;
      white-space: nowrap;
      color: var(--muted);
    }

    .summary-chip strong { color: var(--text); }
    .summary-chip[data-tone="ok"] { border-color: var(--ok); }
    .summary-chip[data-tone="warn"] { border-color: var(--warn); color: var(--warn); }
    .summary-chip[data-tone="danger"] { border-color: var(--danger); color: var(--danger); }

    .workbench {
      min-height: 0;
      min-width: 0;
      display: grid;
      grid-template-columns: 132px minmax(0, 1fr);
      grid-template-rows: minmax(0, 1fr);
      padding: 0;
      background: var(--surface);
    }

    .inspector-rail {
      min-height: 0;
      overflow: auto;
      display: grid;
      grid-template-rows: auto auto auto minmax(0, 1fr);
      align-content: start;
      gap: 7px;
      padding: 7px;
      border-right: 1px solid var(--line);
      background: var(--chrome);
    }

    .rail-guide {
      width: 100%;
      display: grid;
      grid-template-columns: auto minmax(0, 1fr);
      grid-template-areas:
        "icon label"
        "icon hint";
      align-items: center;
      column-gap: 7px;
      row-gap: 1px;
      min-height: 46px;
      padding: 7px;
      border: 1px solid var(--line);
      border-radius: 7px;
      background: var(--surface);
      color: var(--text);
      text-align: left;
    }

    .rail-guide .svg-icon { grid-area: icon; }

    .rail-guide span {
      grid-area: label;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 12px;
      font-weight: 750;
    }

    .rail-guide small {
      grid-area: hint;
      color: var(--muted);
    }

    .inspector-rail__modes,
    .inspector-rail__scenarios,
    .inspector-rail__summary {
      display: grid;
      gap: 5px;
    }

    .inspector-rail__modes b,
    .inspector-rail__scenarios b,
    .inspector-rail__summary b {
      color: var(--muted);
      padding: 0 2px;
      font-size: 10px;
      line-height: 1.2;
      text-transform: uppercase;
    }

    .inspector-rail__scenarios button,
    .rail-action {
      width: 100%;
      min-height: 31px;
      display: grid;
      grid-template-columns: 18px minmax(0, 1fr) auto;
      align-items: center;
      gap: 6px;
      border: 1px solid transparent;
      border-radius: 6px;
      background: transparent;
      color: var(--muted);
      font-size: 11px;
      font-weight: 700;
      text-align: left;
      padding: 0 7px;
    }

    .inspector-rail__scenarios button span,
    .rail-action span {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .rail-action em {
      min-width: 17px;
      min-height: 17px;
      display: inline-grid;
      place-items: center;
      border-radius: 999px;
      background: var(--surface-soft);
      color: var(--muted);
      font-size: 9px;
      font-style: normal;
      font-weight: 800;
    }

    .inspector-rail__scenarios button:hover,
    .rail-action:hover,
    .rail-action.icon-btn--active {
      background: var(--surface) !important;
      border-color: var(--line) !important;
      color: var(--text) !important;
    }

    .inspector-rail__summary .summary-chip {
      width: 100%;
      min-height: 38px;
      display: grid;
      grid-template-columns: 18px minmax(0, 1fr);
      grid-template-areas:
        "icon value"
        "icon label";
      align-items: center;
      justify-items: start;
      gap: 0;
      border-radius: 6px;
      padding: 4px 7px;
      text-align: left;
    }

    .inspector-rail__summary .summary-chip .svg-icon { grid-area: icon; }
    .inspector-rail__summary .summary-chip strong { grid-area: value; }

    .inspector-rail__summary .summary-chip span {
      grid-area: label;
      max-width: 100%;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 10px;
      line-height: 1.1;
    }

    .workbench__main {
      min-width: 0;
      min-height: 0;
      display: grid;
      grid-template-rows: minmax(0, 1fr);
    }

    .badge--ok {
      color: var(--ok);
      border-color: var(--ok);
      border-radius: 999px;
    }

    .workbench__body {
      min-width: 0;
      min-height: 0;
      display: grid;
      grid-template-columns: minmax(360px, 1fr) minmax(250px, 34%);
      border: 0;
      border-radius: 0;
      overflow-x: auto;
      overflow-y: hidden;
      background: var(--surface);
    }

    .workbench-splitter {
      min-width: 7px;
      width: 7px;
      min-height: 0;
      border: 0;
      border-left: 1px solid var(--line);
      border-right: 1px solid var(--line);
      background: var(--chrome);
      cursor: col-resize;
      padding: 0;
      position: relative;
      display: grid;
      place-items: center;
      touch-action: none;
    }

    .workbench-splitter span {
      width: 2px;
      height: 48px;
      border-radius: 999px;
      background: var(--line-strong);
    }

    .workbench-splitter:hover,
    .workbench-splitter:focus-visible,
    .workbench__body[data-split-dragging="true"] .workbench-splitter {
      background: color-mix(in srgb, var(--accent) 12%, var(--chrome));
      outline: none;
    }

    .workbench-splitter:hover span,
    .workbench-splitter:focus-visible span,
    .workbench__body[data-split-dragging="true"] .workbench-splitter span {
      background: var(--accent);
    }

    .conversation {
      min-width: 0;
      min-height: 0;
      overflow: hidden;
      display: grid;
      grid-template-rows: 32px minmax(0, 1fr) auto;
      background: var(--bg);
    }

    .context__body {
      min-width: 0;
      min-height: 0;
      overflow: auto;
      padding: 9px;
      background: var(--bg);
    }

    .conversation__topline {
      min-width: 0;
      min-height: 32px;
      display: grid;
      grid-template-columns: auto minmax(0, 1fr) auto auto auto;
      align-items: center;
      gap: 6px;
      padding: 0 8px;
      border-bottom: 1px solid var(--line);
      background: var(--chrome);
      color: var(--muted);
      font-size: 12px;
    }

    .conversation__topline span:nth-child(2) {
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
    }

    .conversation__topline button {
      min-height: 23px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: var(--surface);
      color: var(--text);
      padding: 0 7px;
      font-size: 11px;
      display: inline-flex;
      align-items: center;
      gap: 5px;
    }

    .conversation__topline button .svg-icon {
      width: 13px;
      height: 13px;
    }

    .conversation__scroll {
      min-height: 0;
      overflow: auto;
      padding: 8px 12px 4px;
    }

    .scenario-row__active {
      border-color: var(--accent) !important;
      color: var(--accent) !important;
    }

    .run-marker {
      width: 100%;
      display: flex;
      align-items: center;
      gap: 8px;
      color: var(--muted);
      font-size: 12px;
      margin: 4px 0 8px;
      border: 0;
      background: transparent;
      padding: 0;
      text-align: center;
    }

    .run-marker::before,
    .run-marker::after {
      content: "";
      height: 1px;
      background: var(--line);
      flex: 1;
    }

    .run-marker span {
      border: 1px solid var(--line);
      border-radius: 999px;
      background: var(--surface);
      padding: 4px 9px;
      white-space: nowrap;
    }

    .run-marker b {
      color: var(--accent);
      font-size: 11px;
      font-weight: 700;
    }

    .run-marker--open span {
      border-color: color-mix(in srgb, var(--accent) 45%, var(--line));
      color: var(--accent);
    }

    .run-popover {
      margin: -2px auto 10px;
      width: min(620px, 100%);
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
      overflow: hidden;
    }

    .run-popover header,
    .run-popover footer {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
      padding: 7px 9px;
      background: var(--chrome);
      border-bottom: 1px solid var(--line);
      font-size: 12px;
    }

    .run-popover header span {
      color: var(--muted);
    }

    .run-popover footer {
      border-top: 1px solid var(--line);
      border-bottom: 0;
      justify-content: flex-end;
      background: var(--surface);
    }

    .run-popover footer button {
      border: 1px solid var(--line);
      border-radius: 6px;
      background: var(--surface-soft);
      color: var(--text);
      min-height: 25px;
      padding: 2px 8px;
      font-size: 12px;
    }

    .run-popover__grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 1px;
      background: var(--line);
    }

    .run-popover__grid div {
      min-width: 0;
      display: grid;
      gap: 2px;
      padding: 8px;
      background: var(--surface);
    }

    .run-popover__grid span {
      color: var(--muted);
      font-size: 10px;
      text-transform: uppercase;
      font-weight: 700;
    }

    .run-popover__grid b {
      overflow: hidden;
    }

    .turn {
      display: grid;
      grid-template-columns: 30px minmax(0, 1fr);
      gap: 9px;
      margin: 10px 0;
    }

    .turn[data-actor="user"] {
      grid-template-columns: minmax(0, 650px);
      justify-content: end;
    }

    .actor-avatar {
      width: 28px;
      height: 28px;
      display: grid;
      place-items: center;
      color: var(--surface);
      background: var(--accent);
      font-weight: 700;
      font-size: 10px;
      margin-top: 2px;
      position: relative;
      flex: 0 0 auto;
    }

    .actor-avatar .svg-icon {
      width: 13px;
      height: 13px;
      color: var(--surface);
    }

    .actor-avatar i {
      position: absolute;
      right: -3px;
      bottom: -3px;
      width: 12px;
      height: 12px;
      border-radius: 999px;
      background: var(--surface);
      color: var(--text);
      border: 1px solid var(--line);
      display: grid;
      place-items: center;
      font-size: 9px;
      font-style: normal;
      font-weight: 800;
    }

    .actor-avatar[data-shape="circle"] { border-radius: 50%; }
    .actor-avatar[data-shape="rounded"] { border-radius: 8px; }
    .actor-avatar[data-shape="square"] { border-radius: 3px; }
    .actor-avatar[data-shape="hex"] { clip-path: polygon(25% 6%, 75% 6%, 100% 50%, 75% 94%, 25% 94%, 0% 50%); border-radius: 0; }
    .actor-avatar[data-shape="shield"] { clip-path: polygon(50% 0, 100% 18%, 100% 65%, 50% 100%, 0 65%, 0 18%); border-radius: 0; }
    .actor-avatar[data-shape="triangle"] { clip-path: polygon(50% 6%, 100% 96%, 0 96%); border-radius: 0; }
    .actor-avatar[data-shape="pill"] { border-radius: 999px; }

    .actor-avatar[data-shape="hex"] i,
    .actor-avatar[data-shape="shield"] i,
    .actor-avatar[data-shape="triangle"] i { display: none; }

    .turn[data-actor="user"] .turn__avatar { display: none; }
    .turn[data-actor="agent"] .turn__avatar { background: var(--accent); }
    .turn[data-actor="orchestrator"] .turn__avatar { background: var(--purple); }
    .turn[data-actor="supervisor"] .turn__avatar { background: var(--warn); }
    .turn[data-actor="support"] .turn__avatar { background: var(--teal); }
    .turn[data-actor="tool"] .turn__avatar { background: color-mix(in srgb, var(--text) 70%, var(--muted)); }
    .turn[data-actor="system"] .turn__avatar { background: var(--danger); }

    .turn__body {
      border: 1px solid var(--line);
      border-left: 3px solid var(--line-strong);
      border-radius: 8px;
      background: var(--surface);
      padding: 10px 12px;
      min-width: 0;
      position: relative;
    }

    .turn[data-actor="user"] .turn__body {
      background: var(--surface-soft);
      border-left-color: var(--text);
    }
    .turn[data-actor="agent"] .turn__body { border-left-color: var(--accent); }
    .turn[data-actor="orchestrator"] .turn__body { border-left-color: var(--purple); }
    .turn[data-actor="supervisor"] .turn__body {
      border-left-color: var(--warn);
      background: color-mix(in srgb, var(--warn) 5%, var(--surface));
    }
    .turn[data-actor="support"] .turn__body { border-left-color: var(--teal); }
    .turn[data-actor="tool"] .turn__body {
      border-left-style: dashed;
      border-left-color: var(--muted);
      background: var(--surface-soft);
    }
    .turn[data-actor="system"] .turn__body {
      border-left-color: var(--danger);
      background: color-mix(in srgb, var(--danger) 5%, var(--surface));
    }

    .turn__role {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 1px 7px;
      border: 1px solid var(--line);
      border-radius: 999px;
      background: var(--surface-soft);
      font-size: 10px;
      font-weight: 700;
      color: var(--muted);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .turn[data-actor="user"] .turn__role { background: var(--text); color: var(--surface); border-color: var(--text); }
    .turn[data-actor="agent"] .turn__role { color: var(--accent); border-color: color-mix(in srgb, var(--accent) 50%, var(--line)); }
    .turn[data-actor="orchestrator"] .turn__role { color: var(--purple); border-color: color-mix(in srgb, var(--purple) 50%, var(--line)); }
    .turn[data-actor="supervisor"] .turn__role { color: var(--warn); border-color: color-mix(in srgb, var(--warn) 60%, var(--line)); }
    .turn[data-actor="support"] .turn__role { color: var(--teal); border-color: color-mix(in srgb, var(--teal) 50%, var(--line)); }
    .turn[data-actor="tool"] .turn__role { color: var(--muted); border-style: dashed; }
    .turn[data-actor="system"] .turn__role { color: var(--danger); border-color: color-mix(in srgb, var(--danger) 60%, var(--line)); }

    .turn__target {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 1px 7px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: var(--surface);
      font-size: 11px;
      font-weight: 700;
      color: var(--text);
    }

    .turn__target .svg-icon { width: 12px; height: 12px; }
    .turn__target[data-target="currentRun"] { color: var(--accent); border-color: color-mix(in srgb, var(--accent) 55%, var(--line)); }
    .turn__target[data-target="nextRun"] { color: var(--teal); border-color: color-mix(in srgb, var(--teal) 55%, var(--line)); }
    .turn__target[data-target="orchestrator"] { color: var(--purple); border-color: color-mix(in srgb, var(--purple) 55%, var(--line)); }
    .turn__target[data-target="followUp"] { color: var(--warn); border-color: color-mix(in srgb, var(--warn) 55%, var(--line)); }

    .actor-key {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      padding: 6px 8px;
      margin: 0 0 6px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
      font-size: 11px;
    }

    .actor-key__label {
      color: var(--muted);
      font-size: 10px;
      font-weight: 800;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      margin-right: 2px;
    }

    .actor-key__chip {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      padding: 1px 7px 1px 2px;
      border: 1px solid var(--line);
      border-radius: 999px;
      background: var(--surface-soft);
      color: var(--text);
      white-space: nowrap;
    }

    .actor-key__chip .actor-avatar {
      width: 18px;
      height: 18px;
      margin: 0;
      display: grid !important;
    }

    .actor-key__chip .actor-avatar .svg-icon { width: 10px; height: 10px; }
    .actor-key__chip .actor-avatar i { display: none; }

    .actor-key__chip b {
      font-size: 11px;
      font-weight: 700;
    }

    .actor-key__chip em {
      font-style: normal;
      color: var(--muted);
      font-size: 10px;
      font-weight: 700;
      min-width: 14px;
      text-align: right;
    }

    .actor-key__chip[data-actor="user"] .actor-avatar { background: var(--text); }
    .actor-key__chip[data-actor="agent"] .actor-avatar { background: var(--accent); }
    .actor-key__chip[data-actor="orchestrator"] .actor-avatar { background: var(--purple); }
    .actor-key__chip[data-actor="supervisor"] .actor-avatar { background: var(--warn); }
    .actor-key__chip[data-actor="support"] .actor-avatar { background: var(--teal); }
    .actor-key__chip[data-actor="tool"] .actor-avatar { background: color-mix(in srgb, var(--text) 70%, var(--muted)); }
    .actor-key__chip[data-actor="system"] .actor-avatar { background: var(--danger); }

    .decision {
      display: block;
      margin: 8px 0;
      border: 1px solid var(--line);
      border-left-width: 3px;
      border-radius: 8px;
      background: var(--surface);
      overflow: hidden;
    }

    .decision[data-tone="info"] { border-left-color: var(--purple); }
    .decision[data-tone="warn"] {
      border-left-color: var(--warn);
      background: color-mix(in srgb, var(--warn) 4%, var(--surface));
    }
    .decision[data-tone="danger"] {
      border-left-color: var(--danger);
      background: color-mix(in srgb, var(--danger) 5%, var(--surface));
    }

    .decision__row {
      width: 100%;
      display: grid;
      grid-template-columns: 26px minmax(0, 1.1fr) minmax(0, 1.6fr) auto auto;
      align-items: center;
      gap: 9px;
      padding: 7px 9px;
      border: 0;
      background: transparent;
      color: var(--text);
      text-align: left;
      font-size: 12px;
    }

    .decision__row .actor-avatar {
      width: 22px;
      height: 22px;
      margin: 0;
    }

    .decision[data-actor="orchestrator"] .decision__row .actor-avatar { background: var(--purple); }
    .decision[data-actor="supervisor"] .decision__row .actor-avatar { background: var(--warn); }
    .decision[data-actor="system"] .decision__row .actor-avatar { background: var(--danger); }

    .decision__row .actor-avatar .svg-icon { width: 12px; height: 12px; }
    .decision__row .actor-avatar i { display: none; }

    .decision__lead {
      min-width: 0;
      display: grid;
      gap: 1px;
    }

    .decision__lead strong {
      font-size: 10px;
      font-weight: 800;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      color: var(--muted);
    }

    .decision[data-tone="info"] .decision__lead strong { color: var(--purple); }
    .decision[data-tone="warn"] .decision__lead strong { color: var(--warn); }
    .decision[data-tone="danger"] .decision__lead strong { color: var(--danger); }

    .decision__lead span {
      font-size: 13px;
      font-weight: 650;
      color: var(--text);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .decision__summary {
      min-width: 0;
      color: var(--muted);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 12px;
    }

    .decision__retry {
      color: var(--muted);
      font-size: 11px;
      font-weight: 700;
      padding: 1px 7px;
      border: 1px solid var(--line);
      border-radius: 999px;
      background: var(--surface);
      white-space: nowrap;
    }

    .decision__row b {
      color: var(--accent);
      font-size: 11px;
      font-weight: 800;
    }

    .decision__detail {
      border-top: 1px solid var(--line);
      padding: 9px 11px;
      background: var(--surface);
    }

    .decision__detail dl {
      margin: 0;
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 6px 14px;
    }

    .decision__detail dl > div {
      min-width: 0;
      display: grid;
      gap: 1px;
    }

    .decision__detail dt {
      font-size: 10px;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--muted);
      font-weight: 800;
    }

    .decision__detail dd {
      margin: 0;
      color: var(--text);
      font-size: 12px;
      line-height: 1.4;
    }

    .decision__detail footer {
      display: flex;
      gap: 6px;
      flex-wrap: wrap;
      margin-top: 9px;
      padding-top: 8px;
      border-top: 1px dashed var(--line);
    }

    .decision__detail footer button {
      min-height: 26px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: var(--surface-soft);
      color: var(--text);
      padding: 2px 8px;
      font-size: 11px;
    }

    .turn__body header {
      display: flex;
      align-items: center;
      gap: 7px;
      color: var(--muted);
      font-size: 12px;
      margin-bottom: 5px;
      min-width: 0;
    }

    .turn__body header em {
      margin-left: auto;
      font-style: normal;
      color: var(--faint);
    }

    .turn__body p {
      margin: 0;
      color: var(--text);
      font-size: 15px;
      line-height: 1.45;
    }

    .turn__actions {
      display: flex;
      gap: 6px;
      margin-top: 8px;
      flex-wrap: wrap;
    }

    .tool-burst {
      width: 100%;
      min-height: 38px;
      display: grid;
      grid-template-columns: auto minmax(0, 1fr) auto;
      align-items: center;
      gap: 9px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
      color: var(--muted);
      padding: 7px 9px;
      text-align: left;
      margin: 10px 0;
    }

    .tool-burst span {
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
    }

    .tool-details {
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
      overflow: hidden;
      margin-top: -4px;
    }

    .tool-details div,
    .file-row {
      display: grid;
      grid-template-columns: minmax(110px, 1fr) minmax(0, 2fr) auto;
      gap: 8px;
      min-height: 31px;
      align-items: center;
      padding: 0 8px;
      border-bottom: 1px solid var(--line);
      font-size: 12px;
    }

    .tool-details div:last-child,
    .file-row:last-child { border-bottom: 0; }

    code {
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
      background: var(--surface-soft);
      border-radius: 5px;
      padding: 1px 5px;
      color: var(--text);
    }

    [data-tone="danger"] { color: var(--danger); }
    [data-tone="ok"] { color: var(--ok); }
    [data-tone="warn"] { color: var(--warn); }

    .composer {
      border: 1px solid var(--line-strong);
      border-radius: 8px;
      background: var(--surface);
      margin: 6px 8px 8px;
      overflow: hidden;
    }

    .composer__input {
      min-height: 54px;
      padding: 10px 12px;
      color: var(--faint);
      display: grid;
      align-content: start;
      gap: 8px;
    }

    .composer__input > span {
      min-width: 0;
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
    }

    .composer__mentions,
    .composer__quick,
    .composer__context,
    .composer__runtime,
    .git-actions {
      display: flex;
      align-items: center;
      gap: 5px;
      flex-wrap: wrap;
    }

    .composer__mentions button {
      min-height: 22px;
      border: 1px solid var(--line);
      border-radius: 999px;
      color: var(--accent);
      background: var(--surface);
      padding: 1px 7px;
      font-size: 11px;
    }

    .composer__quick {
      padding: 0 6px 6px;
      border-bottom: 1px solid var(--line);
    }

    .composer__quick button {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      min-height: 26px;
      border-radius: 6px;
      color: var(--muted);
    }

    .composer__quick .svg-icon,
    .composer__bar .svg-icon {
      width: 13px;
      height: 13px;
    }

    .composer__mode--active {
      color: var(--accent) !important;
      border-color: var(--accent) !important;
    }

    .composer__bar {
      min-height: 40px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: 8px;
      padding: 6px;
    }

    .composer__send {
      background: var(--primary) !important;
      color: var(--primary-text) !important;
      border-color: var(--primary) !important;
      display: inline-flex;
      align-items: center;
      gap: 5px;
    }

    .git-actions {
      margin-top: -2px;
      margin-bottom: 9px;
    }

    .git-actions button,
    .function-grid button {
      min-height: 26px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: var(--surface-soft);
      color: var(--text);
      padding: 3px 8px;
      font-size: 12px;
    }

    .function-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 6px;
    }

    .function-grid button {
      min-width: 0;
      display: flex;
      align-items: center;
      justify-content: flex-start;
      gap: 6px;
      text-align: left;
      overflow: hidden;
    }

    .function-grid button span {
      min-width: 0;
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
    }

    .context {
      min-width: 0;
      min-height: 0;
      display: grid;
      grid-template-rows: 30px minmax(0, 1fr);
      border-left: 1px solid var(--line);
      background: var(--surface);
    }

    .git-split {
      min-height: 100%;
      display: grid;
      grid-template-columns: minmax(165px, 34%) minmax(260px, 1fr);
      gap: 8px;
    }

    .context__head,
    .sheet__head {
      min-height: 30px;
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      gap: 8px;
      align-items: center;
      padding: 4px 7px;
      border-bottom: 1px solid var(--line);
      background: var(--chrome);
    }

    .context__head strong,
    .context__head span,
    .sheet__head strong,
    .sheet__head span {
      display: block;
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
    }

    .metric-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 6px;
      margin-bottom: 9px;
    }

    .metric-grid div,
    .context-card,
    .source-card,
    .git-summary,
    .sheet__body article,
    .sheet__summary {
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
      padding: 9px;
    }

    .metric-grid b,
    .metric-grid span,
    .source-card strong,
    .source-card span,
    .source-card code {
      display: block;
    }

    .metric-grid b { font-size: 18px; }
    .metric-grid span,
    .context-card p,
    .git-summary span,
    .source-card span,
    .sheet__summary span {
      color: var(--muted);
      font-size: 12px;
      line-height: 1.4;
    }

    .context-card,
    .source-card,
    .git-summary { margin-bottom: 9px; }

    .context-card h3 {
      margin: 0 0 6px;
      font-size: 13px;
    }

    .context-card p { margin: 0; }

    .file-row {
      width: 100%;
      grid-template-columns: minmax(0, 1fr) auto;
      text-align: left;
      border-radius: 0;
      background: var(--surface);
      color: var(--text);
    }

    .file-row code {
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
    }

    .preview-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 7px;
      margin-bottom: 9px;
    }

    .preview-grid button {
      min-height: 96px;
      display: grid;
      align-content: end;
      text-align: left;
      border: 1px solid var(--line);
      border-radius: 8px;
      color: var(--text);
      background: var(--surface-soft);
      padding: 8px;
    }

    .preview-grid span {
      display: block;
      height: 34px;
      border-radius: 5px;
      background: var(--surface);
      margin-bottom: 18px;
    }

    .token-bars {
      display: grid;
      gap: 8px;
      margin-bottom: 9px;
    }

    .token-bars div,
    .debug-band {
      display: grid;
      grid-template-columns: 88px minmax(0, 1fr) 48px;
      align-items: center;
      gap: 8px;
      color: var(--muted);
      font-size: 12px;
    }

    .token-bars i,
    .debug-band i {
      height: 8px;
      border-radius: 999px;
      background: var(--surface-soft);
      overflow: hidden;
    }

    .token-bars em,
    .debug-band em {
      display: block;
      height: 100%;
      background: linear-gradient(90deg, var(--accent), var(--teal), var(--warn));
    }

    .sheet {
      border-left: 1px solid var(--line);
      border-right: 0;
      display: grid;
      grid-template-rows: auto minmax(0, 1fr) auto;
      transition: opacity 120ms ease;
    }

    .workspace--sheet-closed .sheet {
      opacity: 0;
      pointer-events: none;
    }

    .sheet__body {
      min-height: 0;
      overflow: auto;
      padding: 10px;
      display: grid;
      align-content: start;
      gap: 9px;
    }

    .sheet__body p { margin: 5px 0 0; color: var(--text); font-size: 13px; line-height: 1.4; }
    .sheet__summary { width: 100%; text-align: left; display: grid; gap: 4px; }

    .sheet__composer {
      border-top: 1px solid var(--line);
      padding: 8px;
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      gap: 8px;
      align-items: center;
      background: var(--chrome);
    }

    .sheet__composer span {
      border: 1px solid var(--line);
      border-radius: 8px;
      color: var(--faint);
      min-height: 42px;
      display: flex;
      align-items: center;
      padding: 0 10px;
      background: var(--surface);
    }

    .statusbar {
      grid-column: 2;
      min-height: 22px;
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);
      gap: 8px;
      align-items: center;
      background: #1f6feb;
      color: #fff;
      padding: 0 7px;
      font-size: 11px;
    }

    .statusbar__group {
      min-width: 0;
      display: flex;
      align-items: center;
      gap: 2px;
      overflow: hidden;
    }

    .statusbar__group--center {
      justify-content: center;
    }

    .statusbar__group--right {
      justify-content: flex-end;
    }

    .statusbar button {
      min-width: 0;
      height: 20px;
      display: inline-flex;
      align-items: center;
      gap: 4px;
      border: 0;
      border-radius: 4px;
      background: transparent;
      color: inherit;
      padding: 0 7px;
      font-size: inherit;
      white-space: nowrap;
    }

    .modal {
      position: fixed;
      inset: 0;
      z-index: 20;
      display: grid;
      place-items: center;
      padding: 24px;
      background: rgba(17, 24, 39, 0.55);
    }

    .modal__panel,
    .command {
      width: min(1180px, 94vw);
      max-height: 88vh;
      overflow: auto;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
    }

    .modal__panel header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 12px 14px;
      border-bottom: 1px solid var(--line);
      background: var(--chrome);
    }

    .modal__panel header > div {
      display: flex;
      gap: 6px;
      flex-wrap: wrap;
      justify-content: flex-end;
    }

    .modal__tab--active {
      color: var(--accent) !important;
      border-color: var(--accent) !important;
    }

    .modal__panel--debug {
      height: min(760px, 88vh);
      display: grid;
      grid-template-rows: auto minmax(0, 1fr);
    }

    .modal__panel--guide {
      width: min(920px, 94vw);
    }

    .guide-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 12px;
      padding: 14px;
    }

    .guide-grid article {
      min-width: 0;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface-soft);
      padding: 12px;
    }

    .guide-grid h3 {
      margin: 0 0 8px;
      font-size: 14px;
    }

    .guide-grid p {
      margin: 0;
      color: var(--muted);
      line-height: 1.45;
    }

    .guide-grid code {
      border-radius: 4px;
      background: var(--surface);
      padding: 1px 4px;
    }

    .guide-grid .function-grid {
      margin-top: 10px;
    }

    .debug-grid {
      min-height: 0;
      overflow: auto;
      display: grid;
      grid-template-columns: 280px minmax(0, 1fr) 320px;
      gap: 12px;
      padding: 14px;
    }

    .debug-grid article {
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
      padding: 12px;
      align-self: start;
    }

    .debug-grid h3 { margin: 0 0 10px; font-size: 14px; }
    .debug-grid p { color: var(--muted); line-height: 1.45; margin: 0; }

    .ng-chat-prototype[data-theme="dark"] .workspace,
    .ng-chat-prototype[data-theme="dark"] .conversation,
    .ng-chat-prototype[data-theme="dark"] .context__body {
      background:
        linear-gradient(180deg, color-mix(in srgb, var(--chrome) 24%, transparent), transparent 160px),
        var(--bg);
    }

    .ng-chat-prototype[data-theme="dark"] .activity,
    .ng-chat-prototype[data-theme="dark"] .topbar,
    .ng-chat-prototype[data-theme="dark"] .detail-chrome,
    .ng-chat-prototype[data-theme="dark"] .context__head,
    .ng-chat-prototype[data-theme="dark"] .sheet__head,
    .ng-chat-prototype[data-theme="dark"] .conversation__topline {
      background: color-mix(in srgb, var(--chrome) 86%, var(--surface));
    }

    .ng-chat-prototype[data-theme="dark"] .rail-guide,
    .ng-chat-prototype[data-theme="dark"] .task-card,
    .ng-chat-prototype[data-theme="dark"] .turn__body,
    .ng-chat-prototype[data-theme="dark"] .decision,
    .ng-chat-prototype[data-theme="dark"] .actor-key,
    .ng-chat-prototype[data-theme="dark"] .run-popover,
    .ng-chat-prototype[data-theme="dark"] .tool-burst,
    .ng-chat-prototype[data-theme="dark"] .tool-details,
    .ng-chat-prototype[data-theme="dark"] .composer,
    .ng-chat-prototype[data-theme="dark"] .metric-grid div,
    .ng-chat-prototype[data-theme="dark"] .context-card,
    .ng-chat-prototype[data-theme="dark"] .source-card,
    .ng-chat-prototype[data-theme="dark"] .git-summary,
    .ng-chat-prototype[data-theme="dark"] .sheet__body article,
    .ng-chat-prototype[data-theme="dark"] .sheet__summary,
    .ng-chat-prototype[data-theme="dark"] .modal__panel,
    .ng-chat-prototype[data-theme="dark"] .command {
      background: var(--surface);
      box-shadow: inset 0 1px 0 color-mix(in srgb, #fff 4%, transparent);
    }

    .ng-chat-prototype[data-theme="dark"] .task-card--active,
    .ng-chat-prototype[data-theme="dark"] .rail-action.icon-btn--active,
    .ng-chat-prototype[data-theme="dark"] .scenario-row__active {
      background: color-mix(in srgb, var(--accent) 12%, var(--surface)) !important;
    }

    .ng-chat-prototype[data-theme="dark"] .turn[data-actor="user"] .turn__body,
    .ng-chat-prototype[data-theme="dark"] .actor-key__chip,
    .ng-chat-prototype[data-theme="dark"] .composer__input,
    .ng-chat-prototype[data-theme="dark"] .preview-grid button,
    .ng-chat-prototype[data-theme="dark"] .function-grid button,
    .ng-chat-prototype[data-theme="dark"] .git-actions button,
    .ng-chat-prototype[data-theme="dark"] code {
      background: var(--surface-soft);
    }

    .ng-chat-prototype[data-theme="dark"] .turn[data-actor="user"] .turn__role {
      background: color-mix(in srgb, var(--accent) 24%, var(--surface-soft));
      border-color: color-mix(in srgb, var(--accent) 55%, var(--line));
      color: var(--text);
    }

    .ng-chat-prototype[data-theme="dark"] .actor-key__chip[data-actor="user"] .actor-avatar {
      background: var(--surface-soft);
      color: var(--text);
      border: 1px solid var(--line-strong);
    }

    .ng-chat-prototype[data-theme="dark"] .actor-key__chip[data-actor="user"] .actor-avatar .svg-icon {
      color: var(--text);
    }

    .ng-chat-prototype[data-theme="dark"] .composer__send {
      box-shadow: 0 0 0 1px color-mix(in srgb, var(--accent) 40%, transparent), 0 6px 18px color-mix(in srgb, var(--accent) 18%, transparent);
    }

    .ng-chat-prototype[data-theme="dark"] .sheet__composer {
      background: color-mix(in srgb, var(--chrome) 82%, var(--surface));
    }

    .ng-chat-prototype[data-density="compact"] .workspace {
      grid-template-columns: 144px minmax(590px, 1fr) minmax(272px, 28vw);
    }

    .ng-chat-prototype[data-density="compact"] .workbench { grid-template-columns: 64px minmax(0, 1fr); }
    .ng-chat-prototype[data-density="compact"] .inspector-rail { padding: 5px 4px; gap: 5px; }
    .ng-chat-prototype[data-density="compact"] .rail-guide {
      min-height: 34px;
      grid-template-columns: 1fr;
      grid-template-areas: "icon";
      place-items: center;
      padding: 4px;
    }
    .ng-chat-prototype[data-density="compact"] .rail-guide span,
    .ng-chat-prototype[data-density="compact"] .rail-guide small,
    .ng-chat-prototype[data-density="compact"] .inspector-rail__modes b,
    .ng-chat-prototype[data-density="compact"] .inspector-rail__scenarios b,
    .ng-chat-prototype[data-density="compact"] .inspector-rail__summary b,
    .ng-chat-prototype[data-density="compact"] .inspector-rail__scenarios button span,
    .ng-chat-prototype[data-density="compact"] .rail-action span,
    .ng-chat-prototype[data-density="compact"] .rail-action em,
    .ng-chat-prototype[data-density="compact"] .summary-chip span {
      display: none;
    }
    .ng-chat-prototype[data-density="compact"] .inspector-rail__scenarios button,
    .ng-chat-prototype[data-density="compact"] .rail-action {
      grid-template-columns: 1fr;
      justify-items: center;
      padding: 0;
    }
    .ng-chat-prototype[data-density="compact"] .inspector-rail__summary .summary-chip {
      grid-template-columns: 1fr;
      grid-template-areas:
        "icon"
        "value";
      justify-items: center;
      text-align: center;
    }
    .ng-chat-prototype[data-density="compact"] .inspector-rail__summary .summary-chip { min-height: 31px; }
    .ng-chat-prototype[data-density="compact"] .context__body { padding: 7px; }
    .ng-chat-prototype[data-density="compact"] .turn { margin: 7px 0; }
    .ng-chat-prototype[data-density="compact"] .turn__body { padding: 8px 10px; }
    .ng-chat-prototype[data-density="compact"] .turn__body p { font-size: 14px; line-height: 1.38; }
    .ng-chat-prototype[data-density="compact"] .composer__input { min-height: 34px; padding: 8px 10px; }
    .ng-chat-prototype[data-density="compact"] .composer__quick { display: none; }
    .ng-chat-prototype[data-density="compact"] .composer__bar { min-height: 34px; }
    .ng-chat-prototype[data-density="compact"] .detail-chrome { grid-template-columns: 28px minmax(0, 1fr) auto auto; }
    .ng-chat-prototype[data-density="compact"] .detail-chrome__edit,
    .ng-chat-prototype[data-density="compact"] .detail-chrome__state { display: none; }
    .ng-chat-prototype[data-density="compact"] .actor-key { padding: 4px 6px; gap: 4px; }
    .ng-chat-prototype[data-density="compact"] .actor-key__chip b { display: none; }
    .ng-chat-prototype[data-density="compact"] .decision__row { padding: 5px 7px; gap: 7px; }
    .ng-chat-prototype[data-density="compact"] .decision__summary { font-size: 11px; }
    .ng-chat-prototype[data-density="compact"] .decision__detail { padding: 7px 9px; }

    @media (max-width: 1080px) {
      .workspace,
      .ng-chat-prototype[data-density="compact"] .workspace {
        grid-template-columns: minmax(0, 1fr);
      }

      .task-list,
      .sheet { display: none; }
    }

    @media (max-width: 720px) {
      .ng-chat-prototype {
        grid-template-columns: 1fr;
      }

      .activity { display: none; }
      .topbar,
      .workspace,
      .statusbar { grid-column: 1; }
      .topbar { grid-template-columns: minmax(0, 1fr) auto auto; gap: 4px; }
      .topbar__title span { display: none; }
      .topbar__runline span:nth-child(n+3) { display: none; }
      .statusbar { grid-template-columns: minmax(0, 1fr) auto; }
      .statusbar__group--center { display: none; }
      .statusbar__group:first-child button:nth-child(n+2),
      .statusbar__group--right button:nth-child(2),
      .statusbar__group--right button:nth-child(3) { display: none; }
      .statusbar__group--right button:first-child span {
        max-width: 136px;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .detail { grid-template-rows: auto minmax(0, 1fr); }
      .detail-chrome {
        min-height: 34px;
        grid-template-columns: 28px minmax(0, 1fr) auto;
      }
      .detail-chrome__edit,
      .detail-chrome__state,
      .detail-chrome__complete,
      .project-pill { display: none; }
      .composer__bar { grid-template-columns: 1fr; }
      .workbench,
      .ng-chat-prototype[data-density="compact"] .workbench { grid-template-columns: minmax(0, 1fr); }
      .inspector-rail { display: none; }
      .workbench__body { grid-template-columns: minmax(0, 1fr) !important; }
      .workbench-splitter { display: none; }
      .conversation__topline { grid-template-columns: minmax(0, 1fr) auto; }
      .conversation__topline .badge--ok,
      .conversation__topline button:last-child { display: none; }
      .context { display: none; }
      .turn,
      .turn[data-actor="user"] { grid-template-columns: minmax(0, 1fr); justify-content: stretch; }
      .turn__avatar { display: none; }
      .decision__row { grid-template-columns: 22px minmax(0, 1fr) auto; }
      .decision__summary,
      .decision__retry { display: none; }
      .decision__detail dl { grid-template-columns: 1fr; }
      .actor-key { gap: 4px; padding: 4px 6px; }
      .actor-key__chip b { display: none; }
      .debug-grid { grid-template-columns: 1fr; }
      .guide-grid { grid-template-columns: 1fr; }
      .run-popover__grid,
      .function-grid { grid-template-columns: 1fr; }
    }
  `]
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
  readonly activeActivity = signal<ActivityTarget>('projects');
  readonly chatOpen = signal(true);
  readonly contextPanes = signal<readonly ContextPane[]>(['result']);
  readonly contextOpen = computed(() => this.contextPanes().length > 0);
  readonly splitRatio = signal(54);
  readonly splitDragging = signal(false);
  readonly activeGitFile = signal('frontend/src/app/components/mockups/next-gen-chat-workbench-prototype.component.ts');
  readonly debugTab = signal<DebugTab>('overview');
  readonly composerMode = signal<ComposeMode>('continue');

  readonly iconPaths: Record<string, string[]> = {
    back: ['M19 12H5', 'M12 19l-7-7 7-7'],
    bug: ['M8 2l1.5 2h5L16 2', 'M7 8h10v9a5 5 0 0 1-10 0V8', 'M5 13H2', 'M22 13h-3', 'M5 19H3', 'M21 19h-2', 'M9 12h.01', 'M15 12h.01'],
    chat: ['M21 15a4 4 0 0 1-4 4H8l-5 3V7a4 4 0 0 1 4-4h10a4 4 0 0 1 4 4v8'],
    check: ['M20 6L9 17l-5-5'],
    clock: ['M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20', 'M12 6v6l4 2'],
    close: ['M18 6L6 18', 'M6 6l12 12'],
    code: ['M8 9l-4 3 4 3', 'M16 9l4 3-4 3', 'M14 4l-4 16'],
    columns: ['M4 4h6v16H4z', 'M14 4h6v16h-6z'],
    command: ['M9 7H7a2 2 0 1 1 2-2v14a2 2 0 1 1-2-2h10a2 2 0 1 1-2 2V5a2 2 0 1 1 2 2H9'],
    compress: ['M8 3v5H3', 'M16 3v5h5', 'M8 21v-5H3', 'M16 21v-5h5'],
    copy: ['M8 8h11v11H8z', 'M5 15H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v1'],
    edit: ['M12 20h9', 'M16.5 3.5a2.1 2.1 0 0 1 3 3L8 18l-4 1 1-4 11.5-11.5z'],
    expand: ['M3 8V3h5', 'M21 8V3h-5', 'M3 16v5h5', 'M21 16v5h-5'],
    file: ['M6 2h8l4 4v16H6z', 'M14 2v5h5'],
    fileDiff: ['M6 2h8l4 4v16H6z', 'M14 2v5h5', 'M9 13h6', 'M12 10v6'],
    folder: ['M3 6h7l2 2h9v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'],
    git: ['M6 3v12', 'M18 9v12', 'M6 15a3 3 0 1 0 0 6 3 3 0 0 0 0-6', 'M18 3a3 3 0 1 0 0 6 3 3 0 0 0 0-6', 'M6 7h7a5 5 0 0 1 5 5v3'],
    image: ['M4 5h16v14H4z', 'M8 10a2 2 0 1 0 0-4 2 2 0 0 0 0 4', 'M4 16l4-4 3 3 3-4 6 6'],
    list: ['M8 6h13', 'M8 12h13', 'M8 18h13', 'M3 6h.01', 'M3 12h.01', 'M3 18h.01'],
    moon: ['M21 12.8A8 8 0 1 1 11.2 3 6 6 0 0 0 21 12.8z'],
    panel: ['M4 4h16v16H4z', 'M9 4v16', 'M9 9h11'],
    panelClose: ['M4 4h16v16H4z', 'M15 4v16', 'M10 9l-3 3 3 3'],
    panelOpen: ['M4 4h16v16H4z', 'M9 4v16', 'M14 9l3 3-3 3'],
    pause: ['M8 5v14', 'M16 5v14'],
    play: ['M7 5v14l11-7z'],
    plus: ['M12 5v14', 'M5 12h14'],
    search: ['M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16', 'M21 21l-4.3-4.3'],
    sun: ['M12 18a6 6 0 1 0 0-12 6 6 0 0 0 0 12', 'M12 2v2', 'M12 20v2', 'M4.9 4.9l1.4 1.4', 'M17.7 17.7l1.4 1.4', 'M2 12h2', 'M20 12h2', 'M4.9 19.1l1.4-1.4', 'M17.7 6.3l1.4-1.4'],
    terminal: ['M4 7l5 5-5 5', 'M11 17h9'],
    tokens: ['M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20', 'M8 12h8', 'M12 8v8'],
    warning: ['M12 3l10 18H2z', 'M12 9v5', 'M12 18h.01'],
    user: ['M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8', 'M4 21a8 8 0 0 1 16 0'],
    agent: ['M5 4h14v12H5z', 'M9 8h.01', 'M15 8h.01', 'M9 12h6', 'M9 20l3-4 3 4'],
    compass: ['M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20', 'M16 8l-2 6-6 2 2-6 6-2z'],
    shield: ['M12 3l8 4v6c0 4.5-3.5 8-8 9-4.5-1-8-4.5-8-9V7z', 'M9 12l2 2 4-4'],
    helper: ['M9 4h6l3 4v8a3 3 0 0 1-3 3H9a3 3 0 0 1-3-3V8z', 'M9 12h6', 'M9 16h4'],
    plug: ['M7 8V4', 'M11 8V4', 'M5 8h8v6a4 4 0 1 1-8 0z', 'M9 18v3'],
    rerun: ['M4 12a8 8 0 0 1 14-5l3 3', 'M21 4v6h-6', 'M20 12a8 8 0 0 1-14 5l-3-3', 'M3 20v-6h6'],
    help: ['M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20', 'M9.5 9a2.5 2.5 0 1 1 3.5 2.3c-.9.5-1 1-1 1.7', 'M12 17h.01'],
  };

  readonly activityItems: Array<{ id: ActivityTarget; icon: string; label: string; title: string }> = [
    { id: 'projects', icon: 'folder', label: 'Projects', title: 'Projects and watched paths' },
    { id: 'tasks', icon: 'columns', label: 'Tasks', title: 'Task board and queue' },
    { id: 'search', icon: 'search', label: 'Search', title: 'Search chat and trace' },
    { id: 'git', icon: 'git', label: 'Git', title: 'Git changes' },
    { id: 'qa', icon: 'check', label: 'QA', title: 'QA, tests, and health' },
    { id: 'tokens', icon: 'tokens', label: 'Tokens', title: 'Token usage' },
  ];

  readonly paneButtons: Array<{ id: WorkbenchPane; label: string; short: string; icon: string }> = [
    { id: 'chat', label: 'Toggle chat pane', short: 'Chat', icon: 'chat' },
    { id: 'result', label: 'Result summary', short: 'Result', icon: 'check' },
    { id: 'git', label: 'Git changes', short: 'Git', icon: 'git' },
    { id: 'preview', label: 'Screenshot preview', short: 'Preview', icon: 'image' },
    { id: 'debug', label: 'Debug summary', short: 'Debug', icon: 'bug' },
  ];

  readonly scenarios: Array<{ id: Scenario; label: string; icon: string }> = [
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

  readonly taskCards = [
    { id: 'bridge', title: 'Chat layout integration bridge', state: 'ready', meta: 'order 50', active: false },
    { id: 'projection', title: 'Next-gen chat conversation event projection', state: 'ready', meta: 'order 60', active: true },
    { id: 'tools', title: 'Collapse tool-heavy chat logs into bursts', state: 'ready', meta: 'order 70', active: false },
    { id: 'actors', title: 'Chat actor rails and decision cards', state: 'ready', meta: 'order 80', active: false },
    { id: 'debug', title: 'Fullscreen verbose debug view', state: 'ready', meta: 'order 90', active: false },
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

  readonly featureParity: Array<{ label: string; icon: string; note: string; action: FeatureAction }> = [
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

  readonly gitFiles = [
    { path: 'frontend/src/app/components/mockups/next-gen-chat-workbench-prototype.component.ts', delta: '+812 -0' },
    { path: 'frontend/src/app/app.ts', delta: '+3 -0' },
    { path: 'frontend/src/app/services/feature-flags.service.ts', delta: '+8 -0' },
    { path: 'docs/mockups/chat-window-next-gen/README.md', delta: '+9 -1' },
    { path: 'frontend/e2e/next-gen-chat-angular-prototype.spec.ts', delta: '+82 -0' },
  ];

  readonly screenshots = ['Result split', 'Git split', 'Compact mode', 'Debug modal'];

  readonly tokenRows = [
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

  readonly workbenchColumns = computed(() => {
    const columns: string[] = [];
    if (this.chatOpen()) {
      columns.push(`minmax(320px, ${this.splitRatio()}fr)`);
      if (this.contextPanes().length > 0) columns.push('7px');
    }
    for (const pane of this.contextPanes()) {
      columns.push(pane === 'git' ? 'minmax(430px, 1.25fr)' : 'minmax(255px, .85fr)');
    }
    return columns.length ? columns.join(' ') : 'minmax(0, 1fr)';
  });

  readonly selectedGitFile = computed(() =>
    this.gitFiles.find((file) => file.path === this.activeGitFile()) ?? this.gitFiles[0]
  );

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
      return;
    }
    this.pane.set(pane);
    this.addContextPane(pane);
  }

  isPaneButtonActive(pane: WorkbenchPane): boolean {
    if (pane === 'chat') return this.chatOpen();
    return this.contextPanes().includes(pane);
  }

  togglePane(pane: WorkbenchPane): void {
    if (pane === 'chat') {
      this.toggleChat();
      return;
    }
    if (this.contextPanes().includes(pane)) {
      this.removeContextPane(pane);
      return;
    }
    this.setPane(pane);
  }

  toggleChat(): void {
    if (this.chatOpen() && this.contextPanes().length === 0) {
      this.addContextPane('result');
    }
    this.chatOpen.set(!this.chatOpen());
  }

  closeContextPane(pane: ContextPane): void {
    this.removeContextPane(pane);
  }

  openAllContextPanes(): void {
    this.contextPanes.set(['result', 'git', 'preview', 'debug']);
    this.pane.set('debug');
  }

  paneTitle(pane: ContextPane): string {
    switch (pane) {
      case 'git': return 'Git changes';
      case 'preview': return 'Screenshot preview';
      case 'debug': return 'Debug summary';
      default: return 'Result summary';
    }
  }

  paneSubtitle(pane: ContextPane): string {
    switch (pane) {
      case 'git': return 'Changed files, commits, source diff';
      case 'preview': return 'Durable visual evidence and lightbox';
      case 'debug': return 'Tokens, actors, waits, and raw links';
      default: return 'Human-readable outcome and risk signals';
    }
  }

  private addContextPane(pane: ContextPane): void {
    this.contextPanes.update((panes) => panes.includes(pane) ? panes : [...panes, pane]);
  }

  private removeContextPane(pane: ContextPane): void {
    this.contextPanes.update((panes) => panes.filter((openPane) => openPane !== pane));
    if (!this.chatOpen() && this.contextPanes().length === 0) {
      this.chatOpen.set(true);
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
    if (target === 'tasks') this.toggleStatusPanel('queue');
    if (target === 'search') this.commandOpen.set(true);
    if (target === 'projects') this.sideSheetOpen.set(true);
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
      case 'tokens': return 'Token usage';
      case 'evidence': return 'Visual evidence';
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

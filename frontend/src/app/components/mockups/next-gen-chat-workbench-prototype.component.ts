import { Component, computed, inject, signal } from '@angular/core';
import { FeatureFlagsService } from '../../services/feature-flags.service';

type WorkbenchPane = 'result' | 'git' | 'preview' | 'debug' | 'source' | 'chat';
type Density = 'comfortable' | 'compact';
type Theme = 'light' | 'dark';
type Scenario = 'review' | 'tools' | 'wait' | 'visual' | 'drift';
type DebugTab = 'overview' | 'actors' | 'tools' | 'tokens' | 'trace';
type ComposeMode = 'continue' | 'extend' | 'steer' | 'followup';

interface SummaryChip {
  label: string;
  value: string;
  icon: string;
  pane: WorkbenchPane;
  tone?: 'ok' | 'warn' | 'danger';
}

interface ChatTurn {
  actor: string;
  role: string;
  tone: 'user' | 'agent' | 'orchestrator' | 'qa' | 'system';
  title: string;
  body: string;
  meta?: string;
  actions?: string[];
}

@Component({
  selector: 'app-next-gen-chat-workbench-prototype',
  standalone: true,
  template: `
    <section class="ng-chat-prototype"
             [attr.data-theme]="theme()"
             [attr.data-density]="density()"
             [attr.data-pane]="pane()"
             data-testid="next-gen-chat-angular-prototype">
      <nav class="activity" aria-label="Prototype activity bar">
        @for (item of activityItems; track item.label) {
          <button class="activity__item"
                  [class.activity__item--active]="item.active"
                  [attr.title]="item.title"
                  [attr.aria-label]="item.title">
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
          <span>Next-gen task chat workbench prototype</span>
        </div>
        <div class="topbar__actions">
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
            <span>2-ready</span>
            <strong>Focused queue</strong>
          </div>
          @for (task of taskCards; track task.id) {
            <button class="task-card"
                    [class.task-card--active]="task.active"
                    [attr.data-state]="task.state">
              <span class="task-card__title">{{ task.title }}</span>
              <span class="task-card__meta">{{ task.state }} · {{ task.meta }}</span>
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
            <nav class="detail-chrome__panes" aria-label="Existing task panes">
              @for (panel of detailPanels; track panel.label) {
                <button [class.detail-chrome__pane--active]="panel.active"
                        [attr.title]="panel.title"
                        (click)="panel.pane ? setPane(panel.pane) : null">
                  <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                    @for (path of iconPath(panel.icon); track path) {
                      <path [attr.d]="path"></path>
                    }
                  </svg>
                  <span>{{ panel.label }}</span>
                </button>
              }
            </nav>
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
                <span>Workbench</span>
                <small>controls live here</small>
              </button>
              <nav class="inspector-rail__tabs" aria-label="Task detail tabs">
                <b>Task</b>
                @for (tab of taskTabs; track tab.label) {
                  <button [class.inspector-rail__active]="tab.active"
                          [attr.title]="tab.title"
                          [attr.aria-label]="tab.title">
                    <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                      @for (path of iconPath(tab.icon); track path) {
                        <path [attr.d]="path"></path>
                      }
                    </svg>
                    <span>{{ tab.label }}</span>
                  </button>
                }
              </nav>
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
              <div class="inspector-rail__modes" data-testid="prototype-layout-buttons">
                <b>Split</b>
                @for (mode of paneButtons; track mode.id) {
                  <button class="rail-action"
                          [class.icon-btn--active]="pane() === mode.id"
                          [attr.title]="mode.label"
                          [attr.data-testid]="'prototype-pane-' + mode.id"
                          (click)="setPane(mode.id)">
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
            </aside>

            <div class="workbench__main">
            <div class="workbench__body" [class.workbench__body--chat-only]="pane() === 'chat'">
              <section class="conversation" aria-label="Conversation">
                <div class="conversation__topline">
                  <span class="badge badge--ok">Task Chat</span>
                  <span>{{ scenarioText() }}</span>
                  <button (click)="setPane('source')">
                    <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                      @for (path of iconPath('code'); track path) {
                        <path [attr.d]="path"></path>
                      }
                    </svg>
                    Source map
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
                <div class="run-marker">
                  <span>Run 4 · 12m active · 28 tool calls · 42k tokens · 3 commits</span>
                </div>

                @for (turn of visibleTurns(); track turn.title) {
                  <article class="turn" [attr.data-tone]="turn.tone">
                    <div class="turn__avatar">{{ turn.actor }}</div>
                    <div class="turn__body">
                      <header>
                        <strong>{{ turn.title }}</strong>
                        <span>{{ turn.role }}</span>
                        @if (turn.meta) {
                          <em>{{ turn.meta }}</em>
                        }
                      </header>
                      <p>{{ turn.body }}</p>
                      @if (turn.actions?.length) {
                        <div class="turn__actions">
                          @for (action of turn.actions; track action) {
                            <button (click)="handleAction(action)">{{ action }}</button>
                          }
                        </div>
                      }
                    </div>
                  </article>
                }

                <button class="tool-burst"
                        [class.tool-burst--open]="toolOpen()"
                        (click)="toolOpen.set(!toolOpen())"
                        data-testid="prototype-tool-burst">
                  <strong>Tools 28</strong>
                  <span>read 12 · search 7 · edit 4 · shell 3 · browser 2 · 1 failed · 4 artifacts</span>
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
                  <div class="composer__input">Reply in task context. Use #latest-run, #git, #screenshot, or /create-follow-up...</div>
                  <div class="composer__bar">
                    <div>
                      <button title="Attach context" aria-label="Attach context">
                        <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                          @for (path of iconPath('plus'); track path) {
                            <path [attr.d]="path"></path>
                          }
                        </svg>
                      </button>
                      <button>Task context</button>
                      <button>Full access</button>
                      <button>#ui.html</button>
                    </div>
                    <div>
                      <button>5.5 Extra High</button>
                      <button class="composer__send">
                        <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
                          @for (path of iconPath('play'); track path) {
                            <path [attr.d]="path"></path>
                          }
                        </svg>
                        Run
                      </button>
                    </div>
                  </div>
                </div>
              </section>

              @if (pane() !== 'chat') {
                <aside class="context" aria-label="Workbench context pane" data-testid="prototype-context-pane">
                  <header class="context__head">
                    <div>
                      <strong>{{ contextTitle() }}</strong>
                      <span>{{ contextSubtitle() }}</span>
                    </div>
                    <div>
                      <button class="icon-btn" (click)="setPane('chat')" title="Close pane" aria-label="Close pane">
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
                  @switch (pane()) {
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
                      </section>
                    }
                    @case ('git') {
                      <section class="context__body">
                        <div class="git-summary">
                          <b>3 commits</b>
                          <span>8 files · +624 -91 · no unreviewed conflict</span>
                        </div>
                        @for (file of gitFiles; track file.path) {
                          <button class="file-row">
                            <code>{{ file.path }}</code>
                            <span>{{ file.delta }}</span>
                          </button>
                        }
                        <article class="context-card">
                          <h3>Review rule</h3>
                          <p>This pane previews changes beside chat. The existing Files and Commits tabs remain the canonical deep review surfaces.</p>
                        </article>
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
                    @case ('source') {
                      <section class="context__body">
                        @for (source of sources; track source.name) {
                          <article class="source-card">
                            <strong>{{ source.name }}</strong>
                            <span>{{ source.role }}</span>
                            <code>{{ source.path }}</code>
                          </article>
                        }
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

      <footer class="statusbar">
        <span>main · NextGenChat prototype · {{ density() }} · {{ theme() }}</span>
        <span>42k tokens · 3 commits · 4 screenshots · Ready</span>
      </footer>

      @if (debugOpen()) {
        <div class="modal" data-testid="prototype-debug-modal" (click)="debugOpen.set(false)">
          <section class="modal__panel modal__panel--debug" (click)="$event.stopPropagation()">
            <header>
              <strong>Verbose Debug</strong>
              <div>
                <button>Agent</button>
                <button>Orchestrator</button>
                <button>Tools</button>
                <button>Tokens</button>
                <button (click)="debugOpen.set(false)">Close</button>
              </div>
            </header>
            <div class="debug-grid">
              <article>
                <h3>Actors</h3>
                <div class="metric-grid">
                  <div><b>7</b><span>agent turns</span></div>
                  <div><b>2</b><span>orchestrator</span></div>
                  <div><b>1</b><span>supervisor</span></div>
                  <div><b>1</b><span>QA report</span></div>
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

      @if (commandOpen()) {
        <div class="modal" data-testid="prototype-command-palette" (click)="commandOpen.set(false)">
          <section class="command" (click)="$event.stopPropagation()">
            <input value="workbench: switch to git changes" aria-label="Command palette input" />
            <button (click)="setPane('git'); commandOpen.set(false)">Open Git split</button>
            <button (click)="setPane('preview'); commandOpen.set(false)">Open screenshots</button>
            <button (click)="setPane('debug'); commandOpen.set(false)">Open debug pane</button>
          </section>
        </div>
      }
    </section>
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
      --bg: #11151d;
      --chrome: #171c26;
      --surface: #1d2430;
      --surface-soft: #151b25;
      --line: #30394a;
      --line-strong: #455268;
      --text: #eef3fb;
      --muted: #a3afc2;
      --faint: #727e93;
      --accent: #7aa7ff;
      --ok: #8bd17c;
      --warn: #f3b263;
      --danger: #ff7180;
      --purple: #a990ff;
      --teal: #73d6cc;
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
      grid-template-columns: minmax(0, 1fr) auto;
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
    .topbar__actions { display: flex; gap: 5px; }

    .workspace {
      grid-column: 2;
      min-width: 0;
      min-height: 0;
      display: grid;
      grid-template-columns: 200px minmax(650px, 1fr) minmax(292px, 30vw);
      background: var(--bg);
    }

    .workspace--sheet-closed {
      grid-template-columns: 200px minmax(650px, 1fr) 0;
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
    .detail-head__copy span,
    .context__head span,
    .sheet__head span {
      color: var(--muted);
      font-size: 11px;
    }

    .task-card {
      width: calc(100% - 12px);
      margin: 6px;
      display: grid;
      gap: 4px;
      text-align: left;
      padding: 9px;
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
    }

    .detail {
      min-width: 0;
      min-height: 0;
      display: grid;
      grid-template-rows: minmax(0, 1fr);
      border-right: 1px solid var(--line);
    }

    .detail-head {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      align-items: center;
      gap: 10px;
      padding: 9px 10px;
      background: var(--surface);
      border-bottom: 1px solid var(--line);
    }

    .detail-head h1 {
      margin: 1px 0 0;
      font-size: 15px;
      line-height: 1.2;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .detail-head__chips,
    .workbench__buttons,
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

    .tabs {
      display: flex;
      min-width: 0;
      overflow-x: auto;
      background: var(--chrome);
      border-bottom: 1px solid var(--line);
    }

    .tabs button {
      min-height: 31px;
      border: 0;
      border-right: 1px solid var(--line);
      background: transparent;
      color: var(--muted);
      padding: 0 12px;
      font-size: 12px;
    }

    .tabs__active {
      color: var(--text) !important;
      background: var(--surface) !important;
      box-shadow: inset 0 -2px 0 var(--accent);
    }

    .summary-strip {
      display: flex;
      align-items: center;
      gap: 5px;
      padding: 5px 8px;
      min-height: 34px;
      overflow-x: auto;
      background: var(--bg);
      border-bottom: 1px solid var(--line);
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
    .summary-chip[data-tone="ok"] { border-color: color-mix(in srgb, var(--ok) 48%, var(--line)); }
    .summary-chip[data-tone="warn"] { border-color: color-mix(in srgb, var(--warn) 58%, var(--line)); color: var(--warn); }
    .summary-chip[data-tone="danger"] { border-color: color-mix(in srgb, var(--danger) 58%, var(--line)); color: var(--danger); }

    .workbench {
      min-height: 0;
      min-width: 0;
      display: grid;
      grid-template-columns: 126px minmax(0, 1fr);
      grid-template-rows: minmax(0, 1fr);
      padding: 0;
      background: var(--surface);
    }

    .inspector-rail {
      min-height: 0;
      overflow: auto;
      display: grid;
      grid-template-rows: auto auto auto auto minmax(0, 1fr);
      align-content: start;
      gap: 10px;
      padding: 8px;
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
      font-weight: 700;
    }

    .rail-guide small {
      grid-area: hint;
      color: var(--muted);
      font-size: 10px;
    }

    .inspector-rail__task {
      display: grid;
      gap: 3px;
      color: var(--muted);
      font-size: 10px;
      line-height: 1.2;
    }

    .inspector-rail__task strong {
      color: var(--text);
      font-size: 11px;
      line-height: 1.25;
    }

    .inspector-rail__tabs,
    .inspector-rail__modes,
    .inspector-rail__scenarios,
    .inspector-rail__summary {
      display: grid;
      gap: 5px;
    }

    .inspector-rail__tabs b,
    .inspector-rail__modes b,
    .inspector-rail__scenarios b,
    .inspector-rail__summary b {
      color: var(--muted);
      font-size: 10px;
      font-weight: 800;
      text-transform: uppercase;
    }

    .inspector-rail__tabs button,
    .inspector-rail__scenarios button,
    .rail-action {
      width: 100%;
      min-height: 28px;
      display: grid;
      grid-template-columns: 18px minmax(0, 1fr);
      align-items: center;
      gap: 6px;
      border: 1px solid transparent;
      border-radius: 6px;
      background: transparent;
      color: var(--muted);
      font-size: 11px;
      font-weight: 700;
      text-align: left;
      padding: 0 6px;
    }

    .inspector-rail__tabs button span,
    .inspector-rail__scenarios button span,
    .rail-action span {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .inspector-rail__active,
    .inspector-rail__tabs button:hover,
    .inspector-rail__scenarios button:hover,
    .rail-action:hover,
    .rail-action.icon-btn--active {
      background: var(--surface) !important;
      border-color: var(--line) !important;
      color: var(--text) !important;
    }

    .inspector-rail__summary .summary-chip {
      width: 100%;
      min-height: 40px;
      display: grid;
      grid-template-columns: 18px minmax(0, 1fr);
      grid-template-areas:
        "icon value"
        "icon label";
      align-items: center;
      justify-items: start;
      gap: 0;
      border-radius: 6px;
      padding: 4px 6px;
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
    }

    .workbench__main {
      min-width: 0;
      min-height: 0;
      display: grid;
      grid-template-rows: minmax(0, 1fr);
    }

    .workbench__bar {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      align-items: center;
      gap: 8px;
      min-height: 34px;
      padding: 4px 6px;
      border: 1px solid var(--line);
      border-radius: 8px 8px 0 0;
      background: var(--chrome);
    }

    .workbench__title {
      min-width: 0;
      display: flex;
      align-items: center;
      gap: 7px;
      color: var(--muted);
      font-size: 12px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .badge--ok {
      color: var(--ok);
      border-color: color-mix(in srgb, var(--ok) 42%, var(--line));
      border-radius: 999px;
    }

    .workbench__body {
      min-width: 0;
      min-height: 0;
      display: grid;
      grid-template-columns: minmax(360px, 1fr) minmax(250px, 34%);
      border: 0;
      border-radius: 0;
      overflow: hidden;
      background: var(--surface);
    }

    .workbench__body--chat-only {
      grid-template-columns: minmax(0, 1fr);
    }

    .conversation {
      min-width: 0;
      min-height: 0;
      overflow: hidden;
      display: grid;
      grid-template-rows: 30px minmax(0, 1fr) auto;
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
      min-height: 30px;
      display: grid;
      grid-template-columns: auto minmax(0, 1fr) auto auto;
      align-items: center;
      gap: 7px;
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

    .scenario-row {
      display: flex;
      gap: 5px;
      overflow-x: auto;
      margin-bottom: 8px;
    }

    .scenario-row__active {
      border-color: var(--accent) !important;
      color: var(--accent) !important;
    }

    .source-banner {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      align-items: center;
      gap: 8px;
      padding: 8px 10px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
      color: var(--muted);
      font-size: 13px;
      margin-bottom: 9px;
    }

    .source-banner button {
      border: 1px solid var(--line);
      background: var(--surface-soft);
      border-radius: 6px;
      min-height: 24px;
      color: var(--text);
    }

    .run-marker {
      display: flex;
      align-items: center;
      gap: 8px;
      color: var(--muted);
      font-size: 12px;
      margin: 4px 0 8px;
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

    .turn {
      display: grid;
      grid-template-columns: 30px minmax(0, 1fr);
      gap: 9px;
      margin: 10px 0;
    }

    .turn[data-tone="user"] {
      grid-template-columns: minmax(0, 650px);
      justify-content: end;
    }

    .turn__avatar {
      width: 28px;
      height: 28px;
      border-radius: 50%;
      display: grid;
      place-items: center;
      color: #fff;
      background: var(--accent);
      font-weight: 700;
      font-size: 11px;
      margin-top: 2px;
    }

    .turn[data-tone="user"] .turn__avatar { display: none; }
    .turn[data-tone="orchestrator"] .turn__avatar { background: var(--purple); }
    .turn[data-tone="qa"] .turn__avatar { background: var(--teal); }
    .turn[data-tone="system"] .turn__avatar { background: var(--warn); }

    .turn__body {
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
      padding: 10px 12px;
      min-width: 0;
    }

    .turn[data-tone="user"] .turn__body {
      background: var(--surface-soft);
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
      box-shadow: 0 14px 36px rgba(20, 27, 40, 0.12);
    }

    .composer__input {
      min-height: 42px;
      padding: 10px 12px;
      color: var(--faint);
    }

    .composer__bar {
      min-height: 38px;
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      align-items: center;
      gap: 8px;
      padding: 6px;
    }

    .composer__send {
      background: var(--text) !important;
      color: var(--surface) !important;
      display: inline-flex;
      align-items: center;
      gap: 5px;
    }

    .context {
      min-width: 0;
      min-height: 0;
      display: grid;
      grid-template-rows: 30px minmax(0, 1fr);
      border-left: 1px solid var(--line);
      background: var(--surface);
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
      background: linear-gradient(135deg, color-mix(in srgb, var(--accent) 20%, var(--surface)), var(--surface) 64%);
      padding: 8px;
    }

    .preview-grid span {
      display: block;
      height: 34px;
      border: 1px solid color-mix(in srgb, var(--line) 60%, transparent);
      border-radius: 5px;
      background: color-mix(in srgb, var(--surface) 74%, transparent);
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
      grid-template-columns: minmax(0, 1fr) auto;
      gap: 10px;
      align-items: center;
      background: var(--accent);
      color: #fff;
      padding: 0 8px;
      font-size: 11px;
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
      box-shadow: 0 22px 58px rgba(0, 0, 0, 0.24);
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

    .modal__panel--debug {
      height: min(760px, 88vh);
      display: grid;
      grid-template-rows: auto minmax(0, 1fr);
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

    .modal__panel--image { width: min(980px, 90vw); }
    .modal__panel--guide { width: min(760px, 90vw); }

    .guide-grid {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: 10px;
      padding: 12px;
    }

    .guide-grid article {
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface);
      padding: 12px;
    }

    .guide-grid h3 {
      margin: 0 0 8px;
      font-size: 14px;
    }

    .guide-grid p {
      margin: 0;
      color: var(--muted);
      font-size: 13px;
      line-height: 1.45;
    }

    .image-box {
      min-height: 420px;
      display: grid;
      place-items: center;
      text-align: center;
      background:
        repeating-linear-gradient(90deg, color-mix(in srgb, var(--line) 50%, transparent) 0 1px, transparent 1px 88px),
        var(--bg);
      margin: 14px;
      border: 1px solid var(--line);
      border-radius: 8px;
    }

    .image-box strong,
    .image-box code { display: block; margin: 4px; }

    .command {
      width: min(680px, 92vw);
      padding: 10px;
      display: grid;
      gap: 8px;
    }

    .command input {
      min-height: 42px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--surface-soft);
      color: var(--text);
      padding: 0 12px;
      font: inherit;
    }

    .ng-chat-prototype[data-density="compact"] .workspace {
      grid-template-columns: 184px minmax(590px, 1fr) minmax(260px, 28vw);
    }

    .ng-chat-prototype[data-density="compact"] .workbench { grid-template-columns: 70px minmax(0, 1fr); }
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
    .ng-chat-prototype[data-density="compact"] .inspector-rail__tabs b,
    .ng-chat-prototype[data-density="compact"] .inspector-rail__modes b,
    .ng-chat-prototype[data-density="compact"] .inspector-rail__scenarios b,
    .ng-chat-prototype[data-density="compact"] .inspector-rail__summary b,
    .ng-chat-prototype[data-density="compact"] .inspector-rail__tabs button span,
    .ng-chat-prototype[data-density="compact"] .inspector-rail__scenarios button span,
    .ng-chat-prototype[data-density="compact"] .rail-action span,
    .ng-chat-prototype[data-density="compact"] .summary-chip span {
      display: none;
    }
    .ng-chat-prototype[data-density="compact"] .inspector-rail__tabs button,
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
    .ng-chat-prototype[data-density="compact"] .composer__input { min-height: 34px; }

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
      .topbar__title span { display: none; }
      .detail-head,
      .workbench__bar,
      .composer__bar { grid-template-columns: 1fr; }
      .detail-head__chips { overflow-x: auto; }
      .workbench,
      .ng-chat-prototype[data-density="compact"] .workbench { grid-template-columns: minmax(0, 1fr); }
      .inspector-rail { display: none; }
      .workbench__body { grid-template-columns: minmax(0, 1fr); }
      .conversation__topline { grid-template-columns: minmax(0, 1fr) auto; }
      .conversation__topline .badge--ok,
      .conversation__topline button:last-child { display: none; }
      .context { display: none; }
      .turn,
      .turn[data-tone="user"] { grid-template-columns: minmax(0, 1fr); justify-content: stretch; }
      .turn__avatar { display: none; }
      .summary-strip { scrollbar-width: thin; }
      .debug-grid { grid-template-columns: 1fr; }
      .guide-grid { grid-template-columns: 1fr; }
    }
  `]
})
export class NextGenChatWorkbenchPrototypeComponent {
  private readonly featureFlags = inject(FeatureFlagsService);

  readonly pane = signal<WorkbenchPane>('result');
  readonly density = signal<Density>('comfortable');
  readonly theme = signal<Theme>('light');
  readonly activeScenario = signal<Scenario>('review');
  readonly toolOpen = signal(false);
  readonly sideSheetOpen = signal(true);
  readonly debugOpen = signal(false);
  readonly lightboxOpen = signal(false);
  readonly commandOpen = signal(false);
  readonly guideOpen = signal(false);

  readonly iconPaths: Record<string, string[]> = {
    bug: ['M8 2l1.5 2h5L16 2', 'M7 8h10v9a5 5 0 0 1-10 0V8', 'M5 13H2', 'M22 13h-3', 'M5 19H3', 'M21 19h-2', 'M9 12h.01', 'M15 12h.01'],
    chat: ['M21 15a4 4 0 0 1-4 4H8l-5 3V7a4 4 0 0 1 4-4h10a4 4 0 0 1 4 4v8'],
    check: ['M20 6L9 17l-5-5'],
    clock: ['M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20', 'M12 6v6l4 2'],
    close: ['M18 6L6 18', 'M6 6l12 12'],
    code: ['M8 9l-4 3 4 3', 'M16 9l4 3-4 3', 'M14 4l-4 16'],
    columns: ['M4 4h6v16H4z', 'M14 4h6v16h-6z'],
    command: ['M9 7H7a2 2 0 1 1 2-2v14a2 2 0 1 1-2-2h10a2 2 0 1 1-2 2V5a2 2 0 1 1 2 2H9'],
    compress: ['M8 3v5H3', 'M16 3v5h5', 'M8 21v-5H3', 'M16 21v-5h5'],
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
    play: ['M7 5v14l11-7z'],
    plus: ['M12 5v14', 'M5 12h14'],
    search: ['M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16', 'M21 21l-4.3-4.3'],
    sun: ['M12 18a6 6 0 1 0 0-12 6 6 0 0 0 0 12', 'M12 2v2', 'M12 20v2', 'M4.9 4.9l1.4 1.4', 'M17.7 17.7l1.4 1.4', 'M2 12h2', 'M20 12h2', 'M4.9 19.1l1.4-1.4', 'M17.7 6.3l1.4-1.4'],
    terminal: ['M4 7l5 5-5 5', 'M11 17h9'],
    tokens: ['M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20', 'M8 12h8', 'M12 8v8'],
    warning: ['M12 3l10 18H2z', 'M12 9v5', 'M12 18h.01'],
  };

  readonly activityItems = [
    { icon: 'folder', label: 'Projects', title: 'Projects', active: true },
    { icon: 'columns', label: 'Tasks', title: 'Tasks' },
    { icon: 'search', label: 'Search', title: 'Search' },
    { icon: 'git', label: 'Git', title: 'Git changes' },
    { icon: 'check', label: 'QA', title: 'QA and tests' },
    { icon: 'tokens', label: 'Tokens', title: 'Token usage' },
  ];

  readonly taskTabs = [
    { icon: 'file', label: 'Prompt', title: 'Prompt tab' },
    { icon: 'list', label: 'Log', title: 'Protocol and raw trace tab' },
    { icon: 'chat', label: 'Chat', title: 'Chat tab', active: true },
    { icon: 'fileDiff', label: 'Files', title: 'Files tab' },
    { icon: 'git', label: 'Commits', title: 'Commits tab' },
    { icon: 'image', label: 'Shots', title: 'Screenshots tab' },
  ];

  readonly paneButtons: Array<{ id: WorkbenchPane; label: string; short: string; icon: string }> = [
    { id: 'chat', label: 'Chat only', short: 'Chat', icon: 'chat' },
    { id: 'result', label: 'Result summary', short: 'Result', icon: 'check' },
    { id: 'git', label: 'Git changes', short: 'Git', icon: 'git' },
    { id: 'preview', label: 'Screenshot preview', short: 'Preview', icon: 'image' },
    { id: 'debug', label: 'Debug summary', short: 'Debug', icon: 'bug' },
    { id: 'source', label: 'Source map', short: 'Source', icon: 'code' },
  ];

  readonly scenarios: Array<{ id: Scenario; label: string; icon: string }> = [
    { id: 'review', label: 'Review', icon: 'check' },
    { id: 'tools', label: 'Tools', icon: 'terminal' },
    { id: 'wait', label: 'Wait', icon: 'clock' },
    { id: 'visual', label: 'Images', icon: 'image' },
    { id: 'drift', label: 'Drift', icon: 'warning' },
  ];

  readonly summaryChips: SummaryChip[] = [
    { value: 'Review', label: 'run 4', icon: 'check', pane: 'result', tone: 'ok' },
    { value: '42k', label: 'tokens', icon: 'tokens', pane: 'debug', tone: 'warn' },
    { value: '3', label: 'commits', icon: 'git', pane: 'git' },
    { value: '8', label: 'files', icon: 'fileDiff', pane: 'git' },
    { value: '4', label: 'images', icon: 'image', pane: 'preview' },
    { value: '1', label: 'failed retry', icon: 'warning', pane: 'debug', tone: 'danger' },
    { value: '12m', label: 'active', icon: 'clock', pane: 'result' },
  ];

  readonly taskCards = [
    { id: 'bridge', title: 'Chat layout integration bridge', state: 'ready', meta: 'order 50', active: false },
    { id: 'projection', title: 'Next-gen chat conversation event projection', state: 'ready', meta: 'order 60', active: true },
    { id: 'tools', title: 'Collapse tool-heavy chat logs into bursts', state: 'ready', meta: 'order 70', active: false },
    { id: 'actors', title: 'Chat actor rails and decision cards', state: 'ready', meta: 'order 80', active: false },
    { id: 'debug', title: 'Fullscreen verbose debug view', state: 'ready', meta: 'order 90', active: false },
  ];

  readonly turns: ChatTurn[] = [
    {
      actor: 'Y',
      role: 'Project steering',
      tone: 'user',
      title: 'You',
      body: 'I want to keep the chat open while I inspect the result, Git changes, screenshots, and token pressure beside it.',
    },
    {
      actor: 'A',
      role: 'Task agent',
      tone: 'agent',
      title: 'Task Agent',
      body: 'The workbench keeps chat as the main surface and turns adjacent evidence into deterministic split presets. It avoids a full window manager for the first slice.',
      actions: ['Show technical layer', 'Open Verbose Debug'],
    },
    {
      actor: 'O',
      role: 'Orchestrator',
      tone: 'orchestrator',
      title: 'Orchestrator decision',
      meta: 'retry 1/1',
      body: 'Result, Git, Preview, and Debug are preview panes. The existing Files, Commits, Screenshots, Trace, and token surfaces remain canonical.',
      actions: ['Git split', 'Debug pane'],
    },
    {
      actor: 'Q',
      role: 'Design QA',
      tone: 'qa',
      title: 'QA report',
      body: 'Light mode is primary, dark mode matches hierarchy, mobile collapses to chat, and click interception is covered by Playwright.',
      actions: ['Open screenshots'],
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

  readonly sources = [
    { name: 'Existing Activity parser', role: 'Raw log to events', path: 'frontend/src/app/components/activity-log.parser.ts' },
    { name: 'Protocol pane host', role: 'Current task Activity and composer', path: 'frontend/src/app/components/job-detail/protocol-pane/' },
    { name: 'Git pane', role: 'Canonical file and commit review', path: 'frontend/src/app/components/job-detail/git-pane/' },
    { name: 'Side sheet', role: 'Project-level steering', path: 'frontend/src/app/components/orchestrator-side-sheet/' },
    { name: 'Token surfaces', role: 'Quota and cost context', path: 'frontend/src/app/components/workspace-token-timeline.ts' },
  ];

  readonly visibleTurns = computed(() => {
    const scenario = this.activeScenario();
    if (scenario === 'tools') return this.turns;
    if (scenario === 'wait') {
      return [
        ...this.turns,
        {
          actor: 'S',
          role: 'Supervisor',
          tone: 'system',
          title: 'Supervisor advisory',
          meta: 'quiet 30s',
          body: 'The agent was quiet, then resumed. The default chat shows this as a slim row; Verbose Debug shows the timing band.',
          actions: ['Debug pane'],
        } satisfies ChatTurn,
      ];
    }
    if (scenario === 'visual') {
      return this.turns.map((turn) =>
        turn.tone === 'qa'
          ? { ...turn, body: 'Screenshots are rendered as a compact evidence reel and open into a durable lightbox. Scratch output is never the only evidence path.' }
          : turn
      );
    }
    if (scenario === 'drift') {
      return [
        ...this.turns,
        {
          actor: 'D',
          role: 'System',
          tone: 'system',
          title: 'Schema drift warning',
          meta: 'recoverable',
          body: 'The structured report did not match the JSON contract. The row stays human-readable by default and exposes raw Markdown in Trace.',
          actions: ['Source map'],
        } satisfies ChatTurn,
      ];
    }
    return this.turns;
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
      default:
        return 'Review mode shows the final result, risk signals, token pressure, and evidence shortcuts beside the chat.';
    }
  });

  readonly paneDescription = computed(() => {
    switch (this.pane()) {
      case 'chat': return 'Conversation fills the available task surface.';
      case 'git': return 'Conversation plus Git changes, without leaving the task.';
      case 'preview': return 'Conversation plus screenshot evidence.';
      case 'debug': return 'Conversation plus token and actor summary.';
      case 'source': return 'Conversation plus implementation source map.';
      default: return 'Conversation plus human-readable result summary.';
    }
  });

  readonly contextTitle = computed(() => {
    switch (this.pane()) {
      case 'git': return 'Git changes';
      case 'preview': return 'Screenshot preview';
      case 'debug': return 'Debug summary';
      case 'source': return 'Source map';
      default: return 'Result summary';
    }
  });

  readonly contextSubtitle = computed(() => {
    switch (this.pane()) {
      case 'git': return 'Changed files and commits beside chat';
      case 'preview': return 'Durable visual evidence and lightbox';
      case 'debug': return 'Tokens, actors, waits, and raw links';
      case 'source': return 'Where this becomes real Angular code';
      default: return 'Human-readable outcome and risk signals';
    }
  });

  setPane(pane: WorkbenchPane): void {
    this.pane.set(pane);
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
    if (action === 'Source map') this.setPane('source');
    if (action === 'Show technical layer') this.toolOpen.set(true);
  }

  iconPath(name: string): string[] {
    return this.iconPaths[name] ?? this.iconPaths['panel'];
  }

  close(): void {
    this.featureFlags.setNextGenChatPrototype(false);
  }
}

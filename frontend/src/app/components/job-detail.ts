import { Component, inject, input, output, signal, effect, OnDestroy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobDetail, WatchPathEntry, CliSettings, CliModelInfo, CliType, CLI_TYPES } from '../models/job.model';
import { JobService } from '../services/job.service';
import { ErrorDialogService } from '../services/error-dialog.service';
import { NowTickService } from '../services/now-tick.service';
import {
  formatTokens as fmtTokens,
  formatRateWindow as fmtRateWindow,
  formatResetIn as fmtResetIn,
  stateLabel as fmtStateLabel,
  formatTime as fmtTime,
  formatDate as fmtDate,
  formatDateTime as fmtDateTime,
  formatMultiplier as fmtMultiplier,
  cliTypeLabel as fmtCliTypeLabel
} from '../services/format.util';
import { LayoutPanesService } from './job-detail/layout-panes.service';
import { ClaudeSessionPollService } from './job-detail/claude-session-poll.service';
import { GitPaneService } from './job-detail/git-pane.service';
import { GitPaneComponent } from './job-detail/git-pane/git-pane.component';
import { CliOutputPollService } from './job-detail/cli-output-poll.service';
import { CommandDeckComponent } from './job-detail/command-deck/command-deck.component';
import { PromptPaneComponent } from './job-detail/prompt-pane/prompt-pane.component';
import { LogOverlayComponent } from './job-detail/log-overlay/log-overlay.component';
import { ActivityLogViewComponent } from './activity-log-view';
import { markdownToHtml } from './markdown-utils';

@Component({
  selector: 'app-job-detail',
  standalone: true,
  imports: [FormsModule, ActivityLogViewComponent, GitPaneComponent, CommandDeckComponent, PromptPaneComponent, LogOverlayComponent],
  providers: [LayoutPanesService, ClaudeSessionPollService, GitPaneService, CliOutputPollService],
  template: `
    <div class="detail">
      <header class="detail__header">
        <div class="detail__header-main">
          <button class="detail__back" (click)="back.emit()">←</button>
          <div class="detail__headline">
            @if (editingTitle()) {
              <div class="detail__title-edit">
                <input #titleInput
                       class="detail__title-input"
                       type="text"
                       [value]="titleDraft()"
                       (input)="titleDraft.set($any($event.target).value)"
                       (keydown.enter)="saveTitle()"
                       (keydown.escape)="cancelTitleEdit()"
                       maxlength="200"
                       placeholder="Task title" />
                <div class="detail__title-actions">
                  <button type="button"
                          class="btn-sm btn-sm--primary"
                          [disabled]="savingTitle() || !titleDraft().trim() || titleDraft().trim() === (detail().info.title || detail().info.id)"
                          (click)="saveTitle()">Save</button>
                  <button type="button"
                          class="btn-sm"
                          [disabled]="savingTitle()"
                          (click)="cancelTitleEdit()">Cancel</button>
                </div>
              </div>
            } @else {
              <h2 class="detail__title"
                  (click)="startTitleEdit()"
                  title="Click to rename">
                {{ detail().info.title || detail().info.id }}
                <button type="button"
                        class="detail__title-edit-btn"
                        (click)="$event.stopPropagation(); startTitleEdit()"
                        aria-label="Rename task">✎</button>
              </h2>
            }
          </div>
        </div>
        <span class="detail__state" [class]="'state--' + detail().info.state">
          {{ stateLabel(detail().info.state) }}
        </span>
        @if (isReview()) {
          <button type="button"
                  class="detail__complete-next"
                  data-testid="complete-and-next-btn"
                  [disabled]="completingAndNext()"
                  (click)="completeAndNext()">
            {{ completingAndNext() ? '⏳' : '✓ Complete & Next' }}
          </button>
        }
      </header>

      <app-command-deck
        [currentWatchPath]="detail().info.watchPath"
        [watchPaths]="watchPaths()"
        [cliTypeDraft]="cliTypeDraft()"
        [modelDraft]="modelDraft()"
        [availableModels]="availableModels()"
        [isRunning]="isRunning()"
        [canStart]="canStartJob()"
        [starting]="starting()"
        [elapsedTime]="elapsedTime()"
        (projectChange)="onProjectChange($event)"
        (cliTypeChange)="onCliTypeChange($event)"
        (modelChange)="onModelDraftChange($event)"
        (start)="startJob()"
        (stop)="stopJob()" />


      @if (showCliConfig()) {
        <section class="sidebar-card sidebar-card--toolbar">
          <div class="cli-config">
            <div class="cli-config__header">
              <span class="cli-config__title">🔧 CLI Configuration</span>
              <button class="detail__error-close" (click)="showCliConfig.set(false)">✕</button>
            </div>
            @if (cliStatus()) {
              <div class="cli-config__status" [class.cli-config__status--ok]="cliStatus()!.available"
                   [class.cli-config__status--err]="!cliStatus()!.available">
                @if (cliStatus()!.available) {
                  ✅ {{ cliStatus()!.version }} — {{ cliStatus()!.path }}
                } @else {
                  ❌ Not found at: {{ cliStatus()!.path }}
                }
              </div>
            }
            <div class="cli-config__row">
              <input class="cli-config__input" type="text"
                     [value]="cliPathDraft()"
                     (input)="cliPathDraft.set($any($event.target).value)"
                     placeholder="z.B. copilot, C:\\Program Files\\GitHub\\copilot.exe" />
              <button class="btn-sm" (click)="testCliPath()" [disabled]="cliTesting()">
                {{ cliTesting() ? '⏳' : '🧪 Test' }}
              </button>
              <button class="btn-sm btn-sm--primary" (click)="saveCliPath()" [disabled]="cliTesting()">💾 Save</button>
            </div>
            @if (cliTestResult(); as result) {
              <div class="cli-config__status" [class.cli-config__status--ok]="result.available"
                   [class.cli-config__status--err]="!result.available">
                @if (result.available) {
                  ✅ Found: {{ result.version }}
                } @else {
                  ❌ {{ result.version || 'Not found at this path' }}
                }
              </div>
            }
            <div class="cli-config__separator"></div>
            <div class="cli-config__row">
              <input class="cli-config__input" [type]="showToken() ? 'text' : 'password'"
                     [value]="tokenDraft()"
                     (input)="tokenDraft.set($any($event.target).value)"
                     placeholder="GitHub Token (PAT or OAuth)" />
              <button class="btn-sm" (click)="showToken.set(!showToken())">
                {{ showToken() ? '🙈' : '👁' }}
              </button>
              <button class="btn-sm btn-sm--primary" (click)="saveToken()" [disabled]="tokenSaving()">
                {{ tokenSaving() ? '⏳' : '💾 Save Token' }}
              </button>
            </div>
            @if (cliStatus()) {
              <div class="cli-config__status" [class.cli-config__status--ok]="cliStatus()!.hasToken"
                   [class.cli-config__status--err]="!cliStatus()!.hasToken">
                {{ cliStatus()!.hasToken ? '🔑 Token configured' : '⚠️ No token — CLI may fail to authenticate' }}
              </div>
            }
          </div>
        </section>
      }

      <div class="detail__panes-toolbar">
        <span class="detail__panes-toolbar-label">Panels:</span>
        <button class="btn-sm" [class.btn-sm--primary]="panesVisible().prompt" (click)="togglePane('prompt')" data-testid="pane-toggle-prompt">📝 Task</button>
        <button class="btn-sm" [class.btn-sm--primary]="panesVisible().protocol" (click)="togglePane('protocol')" data-testid="pane-toggle-protocol">🤖 Protocol</button>
        <button class="btn-sm" [class.btn-sm--primary]="panesVisible().git" (click)="togglePane('git')" data-testid="pane-toggle-git">⎇ Git</button>
        <span class="detail__panes-toolbar-spacer"></span>
        <button class="btn-sm" (click)="openInVsCode()" data-testid="open-in-vscode" title="Open the project root in VS Code (-r reuses an existing window).">🪟 VS Code</button>
      </div>

      <div class="detail__panes" [class.detail__panes--maximized]="!!maximizedPane()" data-testid="detail-panes">
        @if (isPaneRendered('prompt')) {
          <app-prompt-pane
            [markdown]="detail().promptMarkdown || ''"
            [maximized]="maximizedPane() === 'prompt'"
            [weight]="paneWeights().prompt"
            [isRunning]="isRunning()"
            (maximizeToggle)="toggleMaximize('prompt')"
            (hide)="togglePane('prompt')"
            (save)="saveFileContent('prompt.md', $event)" />
        }

        @if (!maximizedPane() && panesVisible().prompt && (panesVisible().protocol || panesVisible().git)) {
          <div class="pane__splitter" role="separator" aria-orientation="vertical"
               title="Resize" (pointerdown)="startPaneResize($event, 'prompt', firstVisibleAfter('prompt'))"><span></span></div>
        }

        @if (isPaneRendered('protocol')) {
        <section class="pane pane--protocol" [style.flex]="maximizedPane() ? '1 1 100%' : paneWeights().protocol" data-testid="pane-protocol">
          <header class="pane__header">
            <h3 class="pane__title">🤖 Agent protocol</h3>
            @if (claudeSession(); as cs) {
              @if (cs.error && !cs.turnCount) {
                <span class="pane__telemetry pane__telemetry--err"
                      [title]="'Claude session telemetry: ' + cs.error"
                      data-testid="claude-telemetry-error">⚠ no session yet</span>
              } @else if (cs.turnCount > 0) {
                <span class="pane__telemetry"
                      [title]="claudeSessionTooltip()"
                      data-testid="claude-telemetry">
                  <span class="pane__telemetry-chip">🧠 {{ cs.model || '?' }}</span>
                  <span class="pane__telemetry-chip">↑ {{ formatTokens(cs.inputTokens) }}</span>
                  <span class="pane__telemetry-chip">↓ {{ formatTokens(cs.outputTokens) }}</span>
                  <span class="pane__telemetry-chip pane__telemetry-chip--cache">⚡ {{ formatTokens(cs.cacheReadTokens) }}</span>
                  <span class="pane__telemetry-chip">{{ cs.turnCount }} turns</span>
                </span>
              }
            }
            @if (claudeRateLimit(); as rl) {
              <span class="pane__telemetry pane__telemetry-rate"
                    [class.pane__telemetry-rate--ok]="rl.status === 'allowed'"
                    [class.pane__telemetry-rate--warn]="rl.status && rl.status !== 'allowed'"
                    [title]="rateLimitTooltip()"
                    data-testid="claude-rate-limit">
                <span class="pane__telemetry-chip">⏱ {{ formatRateWindow(rl.window) }} · {{ rl.status || '?' }}</span>
                @if (rl.resetsAt > 0) {
                  <span class="pane__telemetry-chip">reset {{ formatResetIn(rl.resetsAt) }}</span>
                }
                @if (rl.isUsingOverage) {
                  <span class="pane__telemetry-chip pane__telemetry-chip--overage">overage</span>
                }
              </span>
            }
            <button class="pane__maximize"
                    data-testid="pane-maximize-protocol"
                    (click)="toggleMaximize('protocol')"
                    [title]="maximizedPane() === 'protocol' ? 'Restore layout' : 'Maximize'">
              {{ maximizedPane() === 'protocol' ? '⤡' : '⤢' }}
            </button>
            <button class="pane__hide" (click)="togglePane('protocol')" title="Hide panel">×</button>
          </header>
          <div class="pane__body">
          <section class="inspector">
            <div class="inspector__header">
              <div>
                <h3 class="section__title section__title--large">Agent protocol</h3>
              </div>
              <div class="inspector__tabs">
                <button class="inspector__tab"
                        [class.inspector__tab--active]="activeInspectorTab() === 'protocol'"
                        (click)="activeInspectorTab.set('protocol')">
                  Protocol
                </button>
                <button class="inspector__tab"
                        [class.inspector__tab--active]="activeInspectorTab() === 'activity'"
                        (click)="activeInspectorTab.set('activity')">
                  Activity
                </button>
              </div>
            </div>

            <div class="inspector__body">
              @if (activeInspectorTab() === 'protocol') {
                <section class="notes-panel">
                  <div class="notes-panel__header">
                    <div>
                      <h3 class="section__title section__title--large">status.md</h3>
                    </div>
                    <div class="notes-panel__actions">
                      @if (!editingStatus()) {
                        <div class="notes-panel__tabs">
                          <button class="notes-panel__tab"
                                  [class.notes-panel__tab--active]="statusViewMode() === 'preview'"
                                  (click)="statusViewMode.set('preview')">
                            Preview
                          </button>
                          <button class="notes-panel__tab"
                                  [class.notes-panel__tab--active]="statusViewMode() === 'markdown'"
                                  (click)="statusViewMode.set('markdown')">
                            Markdown
                          </button>
                        </div>
                      }
                      @if (editingStatus()) {
                        <div class="section__actions">
                          <button class="btn-sm" (click)="cancelEdit('status')">Cancel</button>
                          <button class="btn-sm btn-sm--primary" (click)="saveFile('status.md')" [disabled]="isRunning()">Save</button>
                        </div>
                      } @else if (isRunning()) {
                        <span class="notes-panel__lock" title="Editing disabled while the CLI is running for this task.">🔒 CLI is running</span>
                      } @else {
                        <button class="btn-sm" (click)="startEdit('status')">✏️ Edit</button>
                      }
                    </div>
                  </div>
                  @if (editingStatus()) {
                    <textarea class="section__editor notes-panel__editor"
                              [(ngModel)]="statusDraftValue"
                              (keydown)="handleFileKeydown($event, 'status.md')"
                              rows="14"></textarea>
                  } @else if (statusViewMode() === 'preview') {
                    <div class="markdown-preview notes-panel__body" [innerHTML]="renderMarkdown(detail().statusMarkdown || '')"></div>
                  } @else {
                    <pre class="markdown-source notes-panel__body">{{ detail().statusMarkdown || '(empty)' }}</pre>
                  }
                </section>
              } @else {
                <div class="inspector__stack">
                  <section class="activity-panel">
                    <div class="activity-panel__header">
                      <div>
                        <h3 class="section__title">Activity log</h3>
                      </div>
                      @if (cliOutput().length > 0 || detail().log.length > 0 || isRunning()) {
                        <button class="btn-sm" (click)="showLogOverlay.set(true)">⤢ Maximize log</button>
                      }
                    </div>

                    @if (cliOutput().length > 0 || isRunning()) {
                      <app-activity-log-view [lines]="cliOutput()" [bodyMaxHeight]="'34vh'" variant="embedded" />
                    } @else {
                      <div class="activity-panel__empty">Start the task to follow the agent output live.</div>
                    }

                    <div class="chat-compose" data-testid="activity-chat-compose">
                      <textarea class="chat-compose__input"
                                data-testid="activity-chat-input"
                                rows="2"
                                placeholder="Type a follow-up — Ctrl+Enter to send. Sends while running pauses the agent first."
                                [value]="followupPrompt()"
                                (input)="followupPrompt.set($any($event.target).value)"
                                (keydown.control.enter)="sendChatMessage()"
                                (keydown.meta.enter)="sendChatMessage()"></textarea>
                      <div class="chat-compose__actions">
                        @if (isRunning()) {
                          <button type="button"
                                  class="btn-sm chat-compose__stop"
                                  data-testid="activity-chat-stop"
                                  (click)="stopJob()">⏸ Pause</button>
                        }
                        <button type="button"
                                class="btn-sm btn-sm--primary chat-compose__send"
                                data-testid="activity-chat-send"
                                [disabled]="!canSendChat()"
                                (click)="sendChatMessage()">
                          {{ chatSendLabel() }}
                        </button>
                      </div>
                    </div>
                  </section>

                  @if (detail().info.lastUsage; as usage) {
                    <section class="sidebar-card sidebar-card--panel activity-metrics">
                      <div class="activity-metrics__row">
                        <span class="activity-metrics__label">Changes</span>
                        <span class="activity-metrics__value">{{ usage.changes || '—' }}</span>
                      </div>
                      @if (detail().info.cliType === 'claude') {
                        <div class="activity-metrics__row">
                          <span class="activity-metrics__label">Tokens</span>
                          <span class="activity-metrics__value">{{ usage.tokens || '—' }}</span>
                        </div>
                      }
                      <div class="activity-metrics__row">
                        <span class="activity-metrics__label">Requests</span>
                        <span class="activity-metrics__value">{{ usage.requests || '—' }}</span>
                      </div>
                    </section>
                  }
                </div>
              }
            </div>
          </section>
          </div>
        </section>
        }

        @if (!maximizedPane() && panesVisible().protocol && panesVisible().git) {
          <div class="pane__splitter" role="separator" aria-orientation="vertical"
               title="Resize" (pointerdown)="startPaneResize($event, 'protocol', 'git')"><span></span></div>
        }

        @if (isPaneRendered('git')) {
          <app-git-pane
            [maximized]="maximizedPane() === 'git'"
            [weight]="paneWeights().git"
            [isRunning]="isRunning()"
            (maximizeToggle)="toggleMaximize('git')"
            (hide)="togglePane('git')" />
        }

        @if (!panesVisible().prompt && !panesVisible().protocol && !panesVisible().git) {
          <div class="detail__panes-empty">All panels hidden — re-enable one above.</div>
        }
      </div>

      @if (showLogOverlay()) {
        <app-log-overlay
          [cliOutput]="cliOutput()"
          [log]="detail().log"
          (close)="showLogOverlay.set(false)" />
      }
    </div>
  `,
  styles: [`
    .session-usage {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: 8px 12px;
      margin: 8px 0;
      font-size: 0.78rem;
      background: rgba(255,255,255,0.03);
      border-radius: 8px;
    }
    .session-usage__row { display: flex; justify-content: space-between; gap: 12px; }
    .session-usage__label { color: rgba(255,255,255,0.55); text-transform: uppercase; letter-spacing: 0.06em; font-size: 0.7rem; }
    .session-usage__value { color: #cdd6f4; font-family: var(--font-mono, monospace); }
    .activity-metrics {
      display: flex;
      flex-direction: row;
      flex-wrap: wrap;
      gap: 8px 24px;
      padding: 10px 14px;
      align-items: baseline;
    }
    .activity-metrics__row { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
    .activity-metrics__label { color: rgba(255,255,255,0.5); text-transform: uppercase; letter-spacing: 0.08em; font-size: 0.65rem; }
    .activity-metrics__value { color: #cdd6f4; font-family: var(--font-mono, monospace); font-size: 0.85rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .chat-compose {
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin-top: 10px;
      padding: 10px;
      border-radius: 10px;
      background: rgba(0,0,0,0.28);
      border: 1px solid rgba(255,255,255,0.08);
    }
    .chat-compose__input {
      width: 100%;
      box-sizing: border-box;
      background: rgba(0,0,0,0.35);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 8px;
      padding: 8px 10px;
      font-family: inherit;
      font-size: 0.85rem;
      resize: vertical;
      min-height: 44px;
    }
    .chat-compose__input:focus { outline: none; border-color: rgba(137,180,250,0.5); }
    .chat-compose__actions {
      display: flex;
      gap: 8px;
      justify-content: flex-end;
      align-items: center;
    }
    .chat-compose__stop {
      background: rgba(245, 194, 231, 0.12);
      color: #f5c2e7;
      border-color: rgba(245, 194, 231, 0.35);
    }
    .chat-compose__send:disabled { opacity: 0.45; cursor: not-allowed; }
    .session-followup { display: flex; flex-direction: column; gap: 8px; margin-top: 8px; }
    .session-followup__input {
      width: 100%;
      box-sizing: border-box;
      background: rgba(0,0,0,0.25);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 8px;
      padding: 8px 10px;
      font-family: inherit;
      font-size: 0.85rem;
      resize: vertical;
    }
    .session-followup__input:focus { outline: none; border-color: rgba(137,180,250,0.5); }
    .cli-picker {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 8px;
    }
    .cli-picker__label {
      font-size: 0.7rem;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: rgba(255,255,255,0.55);
    }
    .cli-picker__group {
      display: inline-flex;
      gap: 2px;
      padding: 2px;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 8px;
      background: rgba(0,0,0,0.25);
    }
    .cli-picker__btn {
      border: 0;
      background: transparent;
      color: #94a3b8;
      padding: 4px 10px;
      font-size: 0.78rem;
      border-radius: 6px;
      cursor: pointer;
    }
    .cli-picker__btn:hover:not(:disabled) {
      color: #e2e8f0;
      background: rgba(255,255,255,0.06);
    }
    .cli-picker__btn--active {
      background: rgba(99,102,241,0.22);
      color: #c7d2fe;
    }
    .cli-picker__btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .model-picker {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 8px;
    }
    .model-picker__label {
      font-size: 0.7rem;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: rgba(255,255,255,0.55);
    }
    .model-picker__select {
      flex: 1;
      background: rgba(0,0,0,0.25);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 8px;
      padding: 6px 10px;
      font-family: inherit;
      font-size: 0.85rem;
    }
    .model-picker__select:disabled { opacity: 0.5; cursor: not-allowed; }
    .model-picker__multiplier {
      display: inline-flex;
      align-items: center;
      padding: 3px 8px;
      border-radius: 999px;
      background: rgba(245, 194, 231, 0.15);
      color: #f5c2e7;
      font-family: var(--font-mono, monospace);
      font-size: 0.75rem;
      font-weight: 600;
      letter-spacing: 0.02em;
    }
    .model-picker__multiplier--free {
      background: rgba(166, 227, 161, 0.18);
      color: #a6e3a1;
    }
    .meta-multiplier {
      display: inline-block;
      margin-left: 6px;
      padding: 2px 6px;
      border-radius: 999px;
      background: rgba(245, 194, 231, 0.15);
      color: #f5c2e7;
      font-family: var(--font-mono, monospace);
      font-size: 0.7rem;
      font-weight: 600;
    }
    .meta-multiplier--free {
      background: rgba(166, 227, 161, 0.18);
      color: #a6e3a1;
    }
    .execution-bar__model {
      margin-left: 10px;
      font-family: var(--font-mono, monospace);
      color: #c4b5fd;
      font-size: 0.8rem;
    }
    /* Command-bar styles moved to ./job-detail/command-deck/command-deck.component.scss */
    .detail {
      background: #181825;
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 24px;
      padding: 18px 20px;
      height: 100%;
      min-height: 0;
      display: flex;
      flex-direction: column;
      gap: 12px;
      position: relative;
      box-sizing: border-box;
    }

    .detail__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 16px;
    }
    .detail__header-main {
      display: flex;
      align-items: center;
      gap: 10px;
      min-width: 0;
      flex: 1;
    }
    .detail__headline {
      min-width: 0;
    }
    .detail__back {
      background: rgba(255,255,255,0.06);
      border: none;
      color: #94a3b8;
      width: 28px; height: 28px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 14px;
      display: grid; place-items: center;
      flex-shrink: 0;
    }
    .detail__back:hover { background: rgba(255,255,255,0.1); }
    .detail__title {
      margin: 0;
      font-size: 18px;
      line-height: 1.25;
      color: #f8fafc;
      word-break: break-word;
      cursor: text;
      display: inline-flex;
      align-items: center;
      gap: 10px;
      border-radius: 6px;
      padding: 2px 6px;
      margin-left: -6px;
      transition: background 0.15s;
    }
    .detail__title:hover { background: rgba(255,255,255,0.04); }
    .detail__title:hover .detail__title-edit-btn { opacity: 1; }
    .detail__title-edit-btn {
      background: rgba(255,255,255,0.08);
      border: 1px solid rgba(255,255,255,0.1);
      color: #94a3b8;
      width: 28px; height: 28px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 14px;
      line-height: 1;
      display: grid; place-items: center;
      opacity: 0;
      transition: opacity 0.15s, background 0.15s, color 0.15s;
      flex-shrink: 0;
    }
    .detail__title-edit-btn:hover {
      background: rgba(99,102,241,0.2);
      color: #c7d2fe;
    }
    .detail__title-edit {
      display: flex;
      flex-direction: column;
      gap: 8px;
      width: 100%;
    }
    .detail__title-input {
      background: rgba(15,23,42,0.6);
      border: 1px solid rgba(99,102,241,0.5);
      color: #f8fafc;
      font-size: 18px;
      line-height: 1.25;
      font-weight: inherit;
      font-family: inherit;
      padding: 6px 10px;
      border-radius: 8px;
      outline: none;
      width: 100%;
      box-sizing: border-box;
    }
    .detail__title-input:focus {
      border-color: #6366f1;
      box-shadow: 0 0 0 3px rgba(99,102,241,0.2);
    }
    .detail__title-actions {
      display: flex;
      gap: 8px;
    }
    .detail__state {
      font-size: 11px;
      text-transform: uppercase;
      padding: 7px 12px;
      border-radius: 999px;
      font-weight: 600;
      letter-spacing: 0.5px;
      flex-shrink: 0;
    }
    .state--1-preparation { background: rgba(139,92,246,0.15); color: #8b5cf6; }
    .state--2-ready { background: rgba(6,182,212,0.15); color: #06b6d4; }
    .state--3-progress { background: rgba(59,130,246,0.15); color: #3b82f6; }
    .state--4-review { background: rgba(245,158,11,0.15); color: #f59e0b; }
    .state--5-completed { background: rgba(16,185,129,0.15); color: #10b981; }
    .state--6-archive { background: rgba(100,116,139,0.15); color: #94a3b8; }
    .detail__complete-next {
      background: rgba(16,185,129,0.12);
      border: 1px solid rgba(16,185,129,0.35);
      color: #10b981;
      padding: 7px 14px;
      border-radius: 999px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 600;
      white-space: nowrap;
      transition: background 0.15s, border-color 0.15s, color 0.15s;
      flex-shrink: 0;
    }
    .detail__complete-next:hover:not(:disabled) {
      background: rgba(16,185,129,0.22);
      border-color: rgba(16,185,129,0.6);
      color: #34d399;
    }
    .detail__complete-next:disabled {
      opacity: 0.5;
      cursor: default;
    }

    .detail__meta {
      display: flex;
      flex-wrap: wrap;
      gap: 8px 16px;
      align-items: center;
    }
    .detail__meta-item {
      display: flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
    }
    .detail__meta-item--project {
      flex: 1 1 220px;
    }
    .detail__meta-label {
      font-size: 10px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      color: #64748b;
    }
    .detail__meta-value {
      font-size: 12px;
      color: #cbd5e1;
    }
    .detail__project-select {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.1);
      color: #e2e8f0;
      padding: 4px 8px;
      border-radius: 8px;
      font-size: 12px;
      cursor: pointer;
      max-width: 280px;
    }
    .detail__project-select:hover { border-color: rgba(255,255,255,0.2); }
    .detail__project-select:focus { outline: none; border-color: #6366f1; }

    .detail__tools {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
      gap: 16px;
    }

    .detail__layout {
      display: grid;
      grid-template-columns: minmax(320px, var(--detail-left, 54%)) 10px minmax(360px, 1fr);
      gap: 10px;
      align-items: start;
      min-height: 0;
      flex: 1;
    }
    /* === 3-pane layout =================================================== */
    .detail__panes-toolbar {
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 6px 4px 10px;
      border-bottom: 1px solid rgba(255,255,255,0.04);
      margin-bottom: 8px;
    }
    .detail__panes-toolbar-label {
      font-size: 11px;
      letter-spacing: 0.04em;
      color: #94a3b8;
      text-transform: uppercase;
      margin-right: 4px;
    }
    .detail__panes-toolbar-spacer { flex: 1; }
    .detail__panes {
      display: flex;
      flex: 1;
      align-items: stretch;
      min-height: 0;
      gap: 0;
    }
    .pane {
      display: flex;
      flex-direction: column;
      min-width: 240px;
      min-height: 0;
      background: rgba(12,12,23,0.55);
      border: 1px solid rgba(255,255,255,0.05);
      border-radius: 16px;
      overflow: hidden;
    }
    .pane__header {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 10px 14px;
      border-bottom: 1px solid rgba(255,255,255,0.04);
      background: rgba(255,255,255,0.02);
    }
    .pane__title {
      flex: 1;
      margin: 0;
      font-size: 13px;
      font-weight: 600;
      letter-spacing: 0.02em;
      color: #cbd5e1;
    }
    .pane__telemetry {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-size: 11px;
      color: #cbd5e1;
      flex-wrap: wrap;
    }
    .pane__telemetry--err { color: #fbbf24; }
    .pane__telemetry-chip {
      display: inline-flex;
      align-items: center;
      gap: 3px;
      padding: 1px 7px;
      border-radius: 999px;
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.06);
      font-family: var(--font-mono, monospace);
    }
    .pane__telemetry-chip--cache { color: #86efac; }
    .pane__telemetry-chip--overage {
      color: #fda4af;
      background: rgba(244,63,94,0.15);
      border-color: rgba(244,63,94,0.35);
    }
    .pane__telemetry-rate--ok   .pane__telemetry-chip { border-color: rgba(34,197,94,0.35); }
    .pane__telemetry-rate--warn .pane__telemetry-chip { border-color: rgba(251,191,36,0.45); color: #fbbf24; }
    .pane__hide,
    .pane__maximize {
      width: 22px;
      height: 22px;
      border-radius: 6px;
      border: 1px solid transparent;
      background: transparent;
      color: #94a3b8;
      cursor: pointer;
      font-size: 14px;
      line-height: 1;
    }
    .pane__hide:hover,
    .pane__maximize:hover {
      color: #f8fafc;
      border-color: rgba(255,255,255,0.12);
      background: rgba(255,255,255,0.04);
    }
    .detail__panes--maximized .pane { min-width: 0; }
    .pane__body {
      flex: 1;
      display: flex;
      flex-direction: column;
      min-height: 0;
      padding: 14px;
      overflow: auto;
    }
    .pane__splitter {
      flex: 0 0 10px;
      align-self: stretch;
      display: flex;
      justify-content: center;
      cursor: col-resize;
      touch-action: none;
    }
    .pane__splitter span {
      width: 2px;
      min-height: 100%;
      border-radius: 999px;
      background: rgba(148,163,184,0.14);
      transition: background 0.15s ease, width 0.15s ease;
    }
    .pane__splitter:hover span { width: 4px; background: rgba(129,140,248,0.55); }
    .detail__panes-empty {
      flex: 1;
      display: flex;
      align-items: center;
      justify-content: center;
      color: #94a3b8;
      font-size: 13px;
    }
    /* Git view styles moved to ./job-detail/git-pane/git-pane.component.scss */
    .detail__splitter {
      align-self: stretch;
      display: flex;
      justify-content: center;
      cursor: col-resize;
      touch-action: none;
      min-height: 100%;
      border-radius: 999px;
    }
    .detail__splitter span {
      width: 2px;
      min-height: 100%;
      border-radius: 999px;
      background: rgba(148,163,184,0.14);
      transition: background 0.15s ease, width 0.15s ease;
    }
    .detail__splitter:hover span {
      width: 4px;
      background: rgba(129,140,248,0.55);
    }
    .detail__main {
      display: flex;
      flex-direction: column;
      min-width: 0;
      min-height: 0;
    }
    .detail__inspector {
      display: flex;
      flex-direction: column;
      min-width: 0;
      min-height: 0;
    }
    .inspector {
      background: rgba(12,12,23,0.55);
      border: 1px solid rgba(255,255,255,0.05);
      border-radius: 24px;
      padding: 20px;
      display: flex;
      flex-direction: column;
      gap: 18px;
      min-height: 0;
      height: 100%;
    }
    .inspector__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 16px;
    }
    .inspector__tabs {
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      justify-content: flex-end;
    }
    .inspector__tab {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.08);
      color: #94a3b8;
      padding: 8px 14px;
      border-radius: 999px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 600;
      transition: background 0.15s ease, border-color 0.15s ease, color 0.15s ease;
    }
    .inspector__tab:hover {
      background: rgba(255,255,255,0.08);
      color: #e2e8f0;
    }
    .inspector__tab--active {
      background: rgba(99,102,241,0.2);
      border-color: rgba(99,102,241,0.4);
      color: #c4b5fd;
    }
    .inspector__body {
      display: flex;
      flex: 1;
      min-height: 0;
    }
    .inspector__body > * {
      flex: 1;
      min-height: 0;
      min-width: 0;
    }
    .notes-panel {
      display: flex;
      flex: 1;
      flex-direction: column;
      min-height: 0;
    }
    .notes-panel__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 12px;
      padding-bottom: 8px;
    }
    .notes-panel__actions {
      display: flex;
      align-items: center;
      justify-content: flex-end;
      gap: 8px;
      flex-wrap: wrap;
    }
    .notes-panel__lock {
      font-size: 11px;
      color: #fbbf24;
      background: rgba(251,191,36,0.10);
      border: 1px solid rgba(251,191,36,0.35);
      border-radius: 999px;
      padding: 2px 8px;
    }
    .notes-panel__tabs {
      display: inline-flex;
      gap: 4px;
      padding: 2px;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 6px;
      background: rgba(255,255,255,0.03);
    }
    .notes-panel__tab {
      border: 0;
      border-radius: 4px;
      color: #94a3b8;
      background: transparent;
      cursor: pointer;
      font-size: 12px;
      padding: 4px 8px;
    }
    .notes-panel__tab--active {
      color: #ddd6fe;
      background: rgba(99,102,241,0.25);
    }
    .notes-panel__body {
      flex: 1;
      min-height: 0;
      overflow: auto;
    }
    .notes-panel__editor {
      flex: 1;
      min-height: 0;
      resize: none;
    }
    .inspector__stack {
      display: flex;
      flex-direction: column;
      gap: 10px;
      min-height: 0;
    }
    .activity-panel {
      display: flex;
      flex: 1;
      flex-direction: column;
      min-height: 0;
      padding: 0;
    }
    .activity-panel__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 12px;
      padding-bottom: 6px;
    }
    .activity-panel__empty {
      padding: 10px 0;
      color: #94a3b8;
      font-size: 13px;
      line-height: 1.5;
    }

    .section {
      margin: 0;
      background: rgba(12,12,23,0.55);
      border: 1px solid rgba(255,255,255,0.05);
      border-radius: 20px;
      padding: 20px;
    }
    .section--primary {
      padding: 24px;
    }
    .section--fill {
      display: flex;
      flex-direction: column;
      min-height: 0;
      height: 100%;
    }
    .section__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 12px;
      margin-bottom: 14px;
    }
    .section__eyebrow {
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      color: #64748b;
      margin-bottom: 4px;
    }
    .section__title {
      font-size: 13px;
      color: #cbd5e1;
      margin: 0;
    }
    .section__title--large {
      font-size: 20px;
      color: #f8fafc;
      line-height: 1.2;
    }
    .markdown-source,
    .markdown-preview {
      margin: 0;
      border: 1px solid rgba(255,255,255,0.05);
      border-radius: 10px;
      background: rgba(0,0,0,0.16);
      color: #cbd5e1;
      padding: 12px 14px;
      font: 13px/1.65 var(--font-mono, 'Consolas', monospace);
      white-space: pre-wrap;
      word-break: break-word;
    }
    .markdown-preview {
      white-space: normal;
      font-family: inherit;
    }
    .markdown-preview :where(h1, h2, h3, h4) {
      margin: 0 0 8px;
      color: #f8fafc;
      line-height: 1.25;
    }
    .markdown-preview :where(p, ul, pre) {
      margin: 0 0 10px;
    }
    .markdown-preview :where(ul) {
      padding-left: 18px;
    }
    .markdown-preview :where(code) {
      color: #c4b5fd;
      background: rgba(124,58,237,0.16);
      border-radius: 4px;
      padding: 1px 4px;
      font-family: var(--font-mono, 'Consolas', monospace);
    }
    .markdown-preview :where(pre) {
      overflow: auto;
      background: rgba(0,0,0,0.22);
      border-radius: 8px;
      padding: 10px;
    }
    .notes-panel__body.markdown-source,
    .notes-panel__body.markdown-preview {
      border: 0;
      border-radius: 0;
      background: transparent;
      padding: 4px 0 0;
    }
    .log {
      display: flex;
      flex-direction: column;
      gap: 8px;
      min-width: 0;
    }
    .log--sidebar {
      max-height: none;
      min-height: 0;
      flex: 1;
      overflow-y: auto;
      padding-right: 4px;
    }
    .log--overlay {
      max-height: calc(100vh - 340px);
      overflow-y: auto;
      padding-right: 6px;
    }
    .log__row {
      display: flex;
      flex-wrap: wrap;
      gap: 12px;
      align-items: baseline;
      padding: 8px 12px;
      background: rgba(0,0,0,0.15);
      border-radius: 10px;
      font-size: 13px;
    }
    .log__time { font-size: 11px; color: #64748b; min-width: 70px; font-variant-numeric: tabular-nums; }
    .log__event { color: #e2e8f0; font-weight: 600; }
    .log__detail { color: #94a3b8; font-size: 12px; }

    .btn-sm {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #94a3b8;
      padding: 4px 10px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 12px;
    }
    .btn-sm:hover { background: rgba(255,255,255,0.1); }
    .btn-sm--primary { background: #6366f1; border-color: #6366f1; color: white; }
    .btn-sm--primary:hover { background: #5558e6; }
    .section__actions { display: flex; gap: 6px; }
    .section__editor {
      width: 100%;
      background: rgba(0,0,0,0.3);
      border: 1px solid rgba(99,102,241,0.4);
      color: #e2e8f0;
      padding: 16px;
      border-radius: 8px;
      font-family: 'Consolas', monospace;
      font-size: 13px;
      line-height: 1.6;
      resize: vertical;
      box-sizing: border-box;
    }
    .section__editor:focus { outline: none; border-color: #6366f1; }
    .section__editor--fill {
      flex: 1;
      min-height: 0;
      resize: none;
    }

    .execution-bar {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 12px;
    }
    .execution-bar__status {
      display: flex;
      align-items: center;
      gap: 10px;
      font-size: 13px;
      color: #94a3b8;
    }
    .execution-bar__pulse {
      width: 8px; height: 8px;
      border-radius: 50%;
      background: #3b82f6;
      animation: pulse 1.5s infinite;
    }
    @keyframes pulse {
      0%, 100% { opacity: 1; box-shadow: 0 0 0 0 rgba(59,130,246,0.4); }
      50% { opacity: 0.7; box-shadow: 0 0 0 6px rgba(59,130,246,0); }
    }
    .execution-bar__text { font-variant-numeric: tabular-nums; }
    /* btn-exec styles moved to command-deck.component.scss */

    .sidebar-card {
      background: rgba(12,12,23,0.55);
      border: 1px solid rgba(255,255,255,0.05);
      border-radius: 20px;
      padding: 18px;
      display: flex;
      flex-direction: column;
      gap: 14px;
      min-width: 0;
    }
    .sidebar-card--toolbar {
      min-height: 100%;
    }
    .sidebar-card--panel {
      flex: 1;
      min-height: 0;
    }
    .sidebar-card__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 12px;
    }
    .sidebar-card__header--clickable {
      cursor: pointer;
      user-select: none;
    }
    .sidebar-card__chevron {
      display: inline-block;
      width: 1em;
      color: #94a3b8;
      margin-right: 4px;
    }
    .sidebar-card__hint {
      margin-left: 8px;
      font-size: 11px;
      font-weight: 400;
      color: #94a3b8;
    }
    .sidebar-card--collapsed {
      padding-bottom: 0;
    }
    .sidebar-card--collapsed .sidebar-card__header {
      align-items: center;
    }
    .sidebar-card__empty {
      padding: 18px 16px;
      border-radius: 12px;
      background: rgba(255,255,255,0.03);
      color: #94a3b8;
      font-size: 13px;
      line-height: 1.5;
    }
    .detail__error-close {
      background: none;
      border: none;
      color: #f87171;
      cursor: pointer;
      font-size: 14px;
      padding: 2px 6px;
      border-radius: 4px;
      flex-shrink: 0;
    }
    .detail__error-close:hover { background: rgba(239,68,68,0.15); }

    .cli-config {
      animation: fadeIn 0.2s ease;
    }
    .cli-config__header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 10px;
    }
    .cli-config__title {
      font-size: 13px;
      font-weight: 600;
      color: #c4b5fd;
    }
    .cli-config__row {
      display: flex;
      gap: 8px;
      align-items: center;
    }
    .cli-config__input {
      flex: 1;
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.12);
      color: #e2e8f0;
      padding: 6px 10px;
      border-radius: 6px;
      font-size: 13px;
      font-family: 'Cascadia Code', 'Fira Code', monospace;
    }
    .cli-config__input:focus {
      outline: none;
      border-color: #6366f1;
    }
    .cli-config__status {
      font-size: 12px;
      padding: 6px 10px;
      border-radius: 6px;
      margin-top: 8px;
    }
    .cli-config__status--ok {
      background: rgba(34,197,94,0.1);
      color: #4ade80;
    }
    .cli-config__status--err {
      background: rgba(239,68,68,0.1);
      color: #fca5a5;
    }
    .cli-config__separator {
      border-top: 1px solid rgba(255,255,255,0.08);
      margin: 8px 0 4px;
    }
    .log-overlay {
      position: fixed;
      inset: 0;
      background: rgba(3,5,10,0.82);
      backdrop-filter: blur(6px);
      display: grid;
      place-items: center;
      padding: 24px;
      z-index: 200;
    }
    .log-overlay__panel {
      width: min(1200px, calc(100vw - 48px));
      max-height: calc(100vh - 48px);
      overflow: hidden;
      background: #11111b;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 24px;
      padding: 24px;
      display: flex;
      flex-direction: column;
      gap: 18px;
      box-shadow: 0 24px 80px rgba(0,0,0,0.55);
    }
    .log-overlay__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 16px;
    }
    .log-overlay__title {
      margin: 0;
      font-size: 26px;
      color: #f8fafc;
    }
    .log-overlay__content {
      display: grid;
      grid-template-columns: minmax(0, 1.15fr) minmax(320px, 0.85fr);
      gap: 18px;
      min-height: 0;
      overflow: auto;
      padding-right: 4px;
    }
    @keyframes fadeIn {
      from { opacity: 0; transform: translateY(-4px); }
      to { opacity: 1; transform: translateY(0); }
    }
    @media (max-width: 1200px) {
      .detail__layout,
      .log-overlay__content {
        grid-template-columns: 1fr;
      }
      .detail__splitter {
        display: none;
      }
      .inspector__header {
        flex-direction: column;
      }
      .inspector__tabs {
        justify-content: flex-start;
      }
    }
    @media (max-width: 720px) {
      .detail {
        padding: 18px;
      }
      .detail__header {
        flex-direction: column;
      }
      .detail__title {
        font-size: 17px;
      }
      .detail__tools {
        grid-template-columns: 1fr;
      }
      .execution-bar {
        flex-direction: column;
        align-items: stretch;
      }
      .cli-config__row {
        flex-wrap: wrap;
      }
      .log-overlay {
        padding: 12px;
      }
      .log-overlay__panel {
        width: calc(100vw - 24px);
        max-height: calc(100vh - 24px);
        padding: 16px;
      }
    }
  `]
})
export class JobDetailComponent implements OnDestroy {
  readonly detail = input.required<JobDetail>();
  readonly watchPaths = input<WatchPathEntry[]>([]);
  readonly back = output<void>();
  readonly fileSaved = output<void>();
  readonly projectChanged = output<string>();
  readonly completeAndNextReview = output<void>();

  readonly editingPrompt = signal(false);
  readonly editingStatus = signal(false);

  // Three-pane layout — state, persistence and resize handlers live in
  // LayoutPanesService (provided locally on this component). The fields
  // below are facades so existing template bindings keep working.
  private readonly layout = inject(LayoutPanesService);
  readonly panesVisible = this.layout.panesVisible;
  readonly paneWeights = this.layout.paneWeights;
  readonly maximizedPane = this.layout.maximizedPane;

  // Live Claude session telemetry — owned by ClaudeSessionPollService
  // (5 s poll, started/stopped in response to detail() changes).
  private readonly claudePoll = inject(ClaudeSessionPollService);
  readonly claudeSession = this.claudePoll.session;
  readonly claudeRateLimit = this.claudePoll.rateLimit;

  // Git view state lives in GitPaneService (provided locally on this
  // component). Facades below keep the existing call sites unchanged.
  private readonly git = inject(GitPaneService);
  readonly gitStatus = this.git.status;
  readonly gitLoading = this.git.loading;
  readonly selectedDiffPath = this.git.selectedDiffPath;
  readonly gitDiffText = this.git.diffText;
  readonly commitMessage = this.git.commitMessage;
  readonly committing = this.git.committing;
  readonly generatingMsg = this.git.generatingMsg;
  // CLI output buffer + run-state lives in CliOutputPollService.
  private readonly cliPoll = inject(CliOutputPollService);
  readonly cliOutput = this.cliPoll.output;
  readonly isRunning = this.cliPoll.isRunning;
  readonly startedAt = this.cliPoll.startedAt;
  readonly elapsedTime = this.cliPoll.elapsedTime;
  readonly errorMsg = signal<string | null>(null);
  readonly starting = signal(false);
  readonly continuing = signal(false);
  readonly followupPrompt = signal('');
  readonly modelDraft = signal('');
  readonly availableModels = signal<CliModelInfo[]>([]);
  readonly cliTypes = CLI_TYPES;
  readonly cliTypeDraft = signal<CliType>('copilot');
  readonly modelCatalogSource = signal<string>('');

  modelMultiplier(id: string | null | undefined): number | null {
    if (!id) return null;
    return this.availableModels().find(m => m.id === id)?.multiplier ?? null;
  }

  formatMultiplier(mult: number | null): string { return fmtMultiplier(mult); }
  readonly showCliConfig = signal(false);
  readonly cliStatus = signal<CliSettings | null>(null);
  readonly cliPathDraft = signal('');
  readonly cliTestResult = signal<CliSettings | null>(null);
  readonly cliTesting = signal(false);
  readonly showLogOverlay = signal(false);
  readonly activeInspectorTab = signal<'protocol' | 'activity'>('protocol');
  readonly tokenDraft = signal('');
  readonly showToken = signal(false);
  readonly tokenSaving = signal(false);
  readonly editingTitle = signal(false);
  readonly titleDraft = signal('');
  readonly savingTitle = signal(false);
  readonly completingAndNext = signal(false);
  readonly statusViewMode = signal<'preview' | 'markdown'>('preview');
  readonly detailPanePercent = this.layout.detailPanePercent;

  // Wall-clock tick used by relative-time formatters (e.g. formatResetIn).
  // Sourced from NowTickService — keeps the formatter stable within one
  // change-detection cycle and avoids the NG0100 minute-boundary trap.
  private readonly nowTick = inject(NowTickService).now;

  promptDraftValue = '';
  statusDraftValue = '';
  private lastCliConfigRequest = 0;
  private currentJobKey: string | null = null;

  constructor(private jobService: JobService, private errorDialog: ErrorDialogService) {
    // Load the initial catalog for whatever CLI the current job uses; the effect below
    // will re-trigger this when the user switches CLIs.
    this.loadModelCatalog('copilot');
  }

  private loadModelCatalog(cliType: CliType) {
    this.jobService.getCliModelCatalog(cliType).subscribe({
      next: (catalog) => {
        const models = catalog.models ?? [];
        this.availableModels.set(models);
        this.modelCatalogSource.set(catalog.source ?? '');
        if (!this.modelDraft()) {
          const def = models.find(m => m.isDefault);
          if (def) this.modelDraft.set(def.id);
        }
      },
      error: () => {
        this.availableModels.set([]);
      }
    });
  }

  onCliTypeChange(value: string) {
    if (!CLI_TYPES.includes(value as CliType)) return;
    const next = value as CliType;
    if (next === this.cliTypeDraft()) return;
    this.cliTypeDraft.set(next);
    // Switching CLI clears the previous model — let the user pick one for the new backend.
    this.modelDraft.set('');
    this.loadModelCatalog(next);

    this.jobService.setJobCliType(this.detail().info.id, next, this.detail().info.watchPath).subscribe({
      next: () => this.fileSaved.emit(),
      error: (err) => this.showError(err)
    });
  }

  cliTypeLabel(t: CliType): string { return fmtCliTypeLabel(t); }

  private detailEffect = effect(() => {
    const d = this.detail();
    const isJobSwitch = this.currentJobKey !== d.info.jobKey;
    this.currentJobKey = d.info.jobKey;
    // Keep GitPaneService in sync with the open job; resets internal
    // state on actual job changes, no-ops on same-job refreshes.
    this.git.setJob(d.info);

    this.errorMsg.set(null);
    if (d.info.model) {
      this.modelDraft.set(d.info.model);
    } else {
      const def = this.availableModels().find(m => m.isDefault);
      this.modelDraft.set(def?.id ?? '');
    }
    const nextCliType = (d.info.cliType ?? 'copilot') as CliType;
    if (nextCliType !== this.cliTypeDraft()) {
      this.cliTypeDraft.set(nextCliType);
      this.loadModelCatalog(nextCliType);
    }

    if (isJobSwitch) {
      // Reset job-scoped UI state only when switching to a different job —
      // refreshes for the same job (e.g. execution status changes) must
      // preserve the live CLI output and view state.
      this.showLogOverlay.set(false);
      this.activeInspectorTab.set('protocol');
      this.showCliConfig.set(false);
      this.cliTestResult.set(null);
      this.editingPrompt.set(false);
      this.editingStatus.set(false);
      this.statusViewMode.set('preview');
      this.editingTitle.set(false);
      this.savingTitle.set(false);
      this.followupPrompt.set('');
      this.cliPoll.resetForJobSwitch();
    }

    this.cliPoll.setJob({ id: d.info.id, watchPath: d.info.watchPath });
    this.applyExecutionState(d.info.execution);

    // The endpoint returns the live buffer while a process is active and falls
    // back to logs/cli-output.log for completed tasks.
    if (d.info.execution?.status === 'running' && !this.cliPoll.isPolling()) {
      this.cliPoll.startPolling();
    }
    this.jobService.getJobOutput(d.info.id, d.info.watchPath).subscribe({
      next: (output) => this.cliPoll.hydrateOutput(output, d.info.execution?.startedAt ?? null),
      error: (err) => {
        if (err.status !== 0) return; // silent for 404 etc
        this.showError(err);
      }
    });

  });
  private cliConfigEffect = effect(() => {
    const requestId = this.errorDialog.cliConfigRequest();
    if (requestId === 0 || requestId === this.lastCliConfigRequest) {
      return;
    }

    this.lastCliConfigRequest = requestId;
    this.openCliConfig();
  });

  ngOnDestroy() {
    this.detailEffect.destroy();
    this.cliConfigEffect.destroy();
    this.cliPoll.stop();
    this.layout.stopLayoutResize();
    this.claudePoll.stop();
  }

  // Bridge detail() changes to the ClaudeSessionPollService. The service
  // ignores no-op syncs and re-arms its 5 s timer only when the polled
  // job actually changes.
  private readonly claudeSessionEffect = effect(() => {
    this.claudePoll.syncTo(this.detail()?.info ?? null);
  });

  canStartJob(): boolean {
    const state = this.detail().info.state;
    return (state === '2-ready' || state === '3-progress') && !this.isRunning();
  }

  startJob(): void {
    this.errorMsg.set(null);
    this.starting.set(true);
    const model = this.modelDraft().trim() || undefined;
    this.jobService.startJob(this.detail().info.id, this.detail().info.watchPath, model).subscribe({
      next: (exec) => {
        this.starting.set(false);
        this.cliPoll.beginRun(new Date(exec.startedAt));
      },
      error: (err) => {
        this.starting.set(false);
        this.showError(err);
      }
    });
  }

  stopJob(): void {
    this.errorMsg.set(null);
    this.jobService.stopJob(this.detail().info.id, this.detail().info.watchPath).subscribe({
      next: () => this.cliPoll.stop(),
      error: (err) => this.showError(err)
    });
  }

  continueJob(): void {
    const prompt = this.followupPrompt().trim();
    if (!prompt) return;

    this.errorMsg.set(null);
    this.continuing.set(true);
    const model = this.modelDraft().trim() || undefined;
    this.jobService.continueJob(this.detail().info.id, prompt, this.detail().info.watchPath, model).subscribe({
      next: (exec) => {
        this.continuing.set(false);
        this.followupPrompt.set('');
        this.cliPoll.beginRun(new Date(exec.startedAt));
      },
      error: (err) => {
        this.continuing.set(false);
        this.showError(err);
      }
    });
  }

  canSendChat(): boolean {
    if (this.continuing()) return false;
    if (!this.followupPrompt().trim()) return false;
    return true;
  }

  chatSendLabel(): string {
    if (this.continuing()) return '⏳ Sending...';
    return this.isRunning() ? '⏸ Pause & Send' : '▶ Send';
  }

  sendChatMessage(): void {
    const prompt = this.followupPrompt().trim();
    if (!prompt || this.continuing()) return;

    if (!this.isRunning()) {
      this.continueJob();
      return;
    }

    // Pause-and-send: stop the running CLI first, then continue with the
    // user's intervention as a follow-up prompt.
    this.errorMsg.set(null);
    this.continuing.set(true);
    this.jobService.stopJob(this.detail().info.id, this.detail().info.watchPath).subscribe({
      next: () => {
        this.isRunning.set(false);
        if (this.pollTimeout) {
          clearTimeout(this.pollTimeout);
          this.pollTimeout = null;
        }
        if (this.elapsedTimer) {
          clearInterval(this.elapsedTimer);
          this.elapsedTimer = null;
        }
        this.continuing.set(false);
        this.continueJob();
      },
      error: (err) => {
        this.continuing.set(false);
        this.showError(err);
      }
    });
  }

  onModelDraftChange(value: string): void {
    const trimmed = (value ?? '').trim();
    this.modelDraft.set(trimmed);
    const current = this.detail().info.model ?? '';
    if (trimmed === current) return;

    this.jobService.setJobModel(
      this.detail().info.id,
      trimmed === '' ? null : trimmed,
      this.detail().info.watchPath
    ).subscribe({
      error: (err) => this.showError(err)
    });
  }

  private showError(err: any): void {
    const message = err.status === 0
      ? 'Backend not reachable — is the API running on localhost:5030?'
      : err.error?.error || (typeof err.error === 'string' ? err.error : `Request failed (${err.status || 'unknown'}): ${err.statusText || err.message || 'Unknown error'}`);

    this.errorMsg.set(message);
    this.errorDialog.show(err, {
      title: 'Task action failed',
      fallbackMessage: message,
      source: `Task ${this.detail().info.id}`,
      canOpenCliConfig: this.isCliErrorMessage(message)
    });
  }

  private applyExecutionState(execution: import('../models/job.model').CliExecution | null): void {
    if (!execution) return;
    this.cliPoll.applyExecution(execution);
    if (execution.status === 'failed') {
      const message = execution.exitCode === null
        ? 'Task execution failed.'
        : `Task execution failed with exit code ${execution.exitCode}.`;
      this.errorMsg.set(message);
      this.errorDialog.show(message, {
        title: 'Task execution failed',
        fallbackMessage: message,
        source: `Task ${this.detail().info.id}`,
        output: { execution, cliOutput: this.cliOutput() }
      });
    }
  }

  isProgress(): boolean {
    return this.detail().info.state === '3-progress';
  }

  isReview(): boolean {
    return this.detail().info.state === '4-review';
  }

  completeAndNext() {
    if (this.completingAndNext()) return;
    this.completingAndNext.set(true);
    const { id, watchPath } = this.detail().info;
    this.jobService.moveJob(id, '5-completed', watchPath).subscribe({
      next: () => {
        this.completingAndNext.set(false);
        this.completeAndNextReview.emit();
      },
      error: (err) => {
        this.completingAndNext.set(false);
        this.errorDialog.show(err, {
          title: 'Failed to complete task',
          fallbackMessage: 'Failed to move task to Completed',
          source: `Task ${id}`
        });
      }
    });
  }

  startEdit(which: 'prompt' | 'status') {
    if (this.isRunning()) return;
    if (which === 'prompt') {
      this.promptDraftValue = this.detail().promptMarkdown ?? '';
      this.editingPrompt.set(true);
    } else {
      this.statusDraftValue = this.detail().statusMarkdown ?? '';
      this.editingStatus.set(true);
    }
  }

  startTitleEdit() {
    if (this.editingTitle()) return;
    this.titleDraft.set(this.detail().info.title || this.detail().info.id);
    this.editingTitle.set(true);
  }

  cancelTitleEdit() {
    this.editingTitle.set(false);
    this.savingTitle.set(false);
  }

  saveTitle() {
    const trimmed = this.titleDraft().trim();
    if (!trimmed || this.savingTitle()) return;
    const current = this.detail().info.title || this.detail().info.id;
    if (trimmed === current) {
      this.editingTitle.set(false);
      return;
    }

    this.savingTitle.set(true);
    this.jobService.setJobTitle(this.detail().info.id, trimmed, this.detail().info.watchPath).subscribe({
      next: () => {
        this.savingTitle.set(false);
        this.editingTitle.set(false);
        this.fileSaved.emit();
      },
      error: (err) => {
        this.savingTitle.set(false);
        this.showError(err);
      }
    });
  }

  cancelEdit(which: 'prompt' | 'status') {
    if (which === 'prompt') this.editingPrompt.set(false);
    else this.editingStatus.set(false);
  }

  saveFile(fileName: string) {
    if (this.isRunning()) return;
    const content = fileName === 'prompt.md' ? this.promptDraftValue : this.statusDraftValue;
    this.saveFileContent(fileName, content);
  }

  saveFileContent(fileName: string, content: string) {
    if (this.isRunning()) return;
    this.jobService.updateJobFile(this.detail().info.id, fileName, content, this.detail().info.watchPath).subscribe({
      next: () => {
        if (fileName === 'prompt.md') this.editingPrompt.set(false);
        else this.editingStatus.set(false);
        this.fileSaved.emit();
      },
      error: (err) => this.showError(err)
    });
  }

  handleFileKeydown(event: KeyboardEvent, fileName: string): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      this.saveFile(fileName);
    }
  }

  renderMarkdown(markdown: string): string {
    return markdownToHtml(markdown);
  }

  // === 3-pane layout — facades for LayoutPanesService ====================

  startLayoutResize(event: PointerEvent): void { this.layout.startLayoutResize(event); }

  togglePane(name: 'prompt' | 'protocol' | 'git'): void {
    const next = this.layout.togglePane(name);
    if (name === 'git' && next.git && !this.gitStatus()) {
      // Lazy-load git status the first time the pane is shown.
      this.refreshGit();
    }
  }

  toggleMaximize(name: 'prompt' | 'protocol' | 'git'): void { this.layout.toggleMaximize(name); }

  isPaneRendered(name: 'prompt' | 'protocol' | 'git'): boolean { return this.layout.isPaneRendered(name); }

  firstVisibleAfter(name: 'prompt' | 'protocol' | 'git'): 'protocol' | 'git' { return this.layout.firstVisibleAfter(name); }

  startPaneResize(event: PointerEvent, left: 'prompt' | 'protocol', right: 'protocol' | 'git'): void {
    this.layout.startPaneResize(event, left, right);
  }

  // === Git view facades ==================================================
  // State + API calls live in GitPaneService (provided locally). The
  // GitPaneComponent in the template binds directly to the service; the
  // wrappers here keep older same-class call sites working (e.g. the
  // togglePane lazy-load below).

  refreshGit(): void { this.git.refresh(); }
  openInVsCode(): void { this.git.openInVsCode(); }

  // === Claude live session telemetry =====================================
  // Polling lives in ClaudeSessionPollService (provided locally on this
  // component); the claudeSessionEffect above bridges detail() changes
  // into it, and the session/rateLimit signals are exposed as facades.

  formatTokens(n: number): string { return fmtTokens(n); }

  claudeSessionTooltip(): string {
    const cs = this.claudeSession();
    if (!cs) return '';
    return [
      `Model: ${cs.model ?? '?'}`,
      `Input: ${cs.inputTokens.toLocaleString()} tokens`,
      `Output: ${cs.outputTokens.toLocaleString()} tokens`,
      `Cache read: ${cs.cacheReadTokens.toLocaleString()} tokens`,
      `Cache creation: ${cs.cacheCreationTokens.toLocaleString()} tokens`,
      `Turns recorded: ${cs.turnCount}`,
      cs.lastTurnAt ? `Last turn: ${cs.lastTurnAt}` : ''
    ].filter(Boolean).join('\n');
  }

  formatRateWindow(window: string | null): string { return fmtRateWindow(window); }

  formatResetIn(epochSeconds: number): string { return fmtResetIn(epochSeconds, this.nowTick()); }

  rateLimitTooltip(): string {
    const rl = this.claudeRateLimit();
    if (!rl) return '';
    const reset = rl.resetsAt
      ? new Date(rl.resetsAt * 1000).toLocaleString()
      : 'unknown';
    return [
      `Window: ${this.formatRateWindow(rl.window)}`,
      `Status: ${rl.status ?? '?'}`,
      `Resets at: ${reset}`,
      `Overage: ${rl.overageStatus ?? '—'}`,
      rl.isUsingOverage ? 'Currently using overage budget' : '',
      `Captured: ${new Date(rl.capturedAt).toLocaleTimeString()}`
    ].filter(Boolean).join('\n');
  }

  stateLabel(state: string): string { return fmtStateLabel(state); }

  formatTime(dateStr: string): string { return fmtTime(dateStr); }

  formatDate(dateStr: string): string { return fmtDate(dateStr); }

  formatDateTime(dateStr: string): string { return fmtDateTime(dateStr); }

  isCliError(): boolean {
    const msg = this.errorMsg();
    return this.isCliErrorMessage(msg);
  }

  openCliConfig(): void {
    this.showCliConfig.set(true);
    this.cliTestResult.set(null);
    this.jobService.getCliSettings().subscribe({
      next: (settings) => {
        this.cliStatus.set(settings);
        this.cliPathDraft.set(settings.path);
      },
      error: (err) => this.showError(err)
    });
  }

  dismissError(): void {
    this.errorMsg.set(null);
    this.showCliConfig.set(false);
  }

  testCliPath(): void {
    const path = this.cliPathDraft().trim();
    if (!path) return;
    this.cliTesting.set(true);
    this.cliTestResult.set(null);
    this.jobService.testCliPath(path).subscribe({
      next: (result) => {
        this.cliTestResult.set(result);
        this.cliTesting.set(false);
      },
      error: (err) => {
        this.cliTesting.set(false);
        this.showError(err);
      }
    });
  }

  saveCliPath(): void {
    const path = this.cliPathDraft().trim();
    if (!path) return;
    this.cliTesting.set(true);
    this.jobService.setCliPath(path).subscribe({
      next: (result) => {
        this.cliStatus.set(result);
        this.cliTestResult.set(null);
        this.cliTesting.set(false);
        if (result.available) {
          this.errorMsg.set(null);
          this.showCliConfig.set(false);
        }
      },
      error: (err) => {
        this.cliTesting.set(false);
        this.showError(err);
      }
    });
  }

  saveToken(): void {
    const token = this.tokenDraft().trim();
    if (!token) return;
    this.tokenSaving.set(true);
    this.jobService.setGitHubToken(token).subscribe({
      next: (result) => {
        this.cliStatus.set(result);
        this.tokenSaving.set(false);
        this.tokenDraft.set('');
        if (result.hasToken && result.available) {
          this.errorMsg.set(null);
        }
      },
      error: (err) => {
        this.tokenSaving.set(false);
        this.showError(err);
      }
    });
  }

  onProjectChange(targetWatchPath: string) {
    if (targetWatchPath === this.detail().info.watchPath) return;
    this.jobService.changeProject(this.detail().info.id, targetWatchPath, this.detail().info.watchPath).subscribe({
      next: () => this.projectChanged.emit(targetWatchPath),
      error: (err) => this.showError(err)
    });
  }

  private isCliErrorMessage(message: string | null | undefined): boolean {
    return !!message && /cli|copilot|authenticat/i.test(message);
  }
}

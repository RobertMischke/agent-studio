import { Component, output } from '@angular/core';

import { USAGE_STRIP } from './next-gen-chat-workbench-prototype.data';
import { StatusPanel } from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'app-found-next-statusbar',
  standalone: true,
  template: `
    <footer class="statusbar" data-testid="prototype-statusbar">
      <div class="statusbar__group">
        <button class="statusbar__item statusbar__item--readonly" (click)="statusPanelRequested.emit('health')" data-testid="prototype-status-health">
          <span class="statusbar__dot statusbar__dot--live"></span>
          <span>2 running</span>
        </button>
        <button class="statusbar__item statusbar__item--readonly" (click)="statusPanelRequested.emit('queue')" data-testid="prototype-status-queue">
          <span class="statusbar__icon-text">auto</span>
          <span>4/6</span>
        </button>
        <button class="statusbar__item" (click)="statusPanelRequested.emit('session')" data-testid="prototype-status-session">
          <span class="statusbar__icon-text">session</span>
          <span>preserved chain 3</span>
        </button>
      </div>
      <div class="statusbar__group statusbar__group--center statusbar__usage">
        @for (item of usageStrip; track item.label) {
          <button class="usage-pill"
                  [attr.data-tone]="item.tone"
                  [attr.title]="item.detail"
                  (click)="statusPanelRequested.emit('tokens')"
                  [attr.data-testid]="item.testId">
            <span>{{ item.label }}</span>
            <b>{{ item.value }}</b>
            @if (item.window) {
              <small>{{ item.window }}</small>
            }
          </button>
        }
        <button class="usage-pill usage-pill--tokens" (click)="statusPanelRequested.emit('tokens')" data-testid="prototype-status-token">
          <span>Tokens</span>
          <b>42k</b>
        </button>
        <button class="usage-pill" (click)="statusPanelRequested.emit('queue')" data-testid="prototype-status-git">
          <span>Git</span>
          <b>3 commits</b>
        </button>
        <button class="usage-pill" (click)="statusPanelRequested.emit('evidence')" data-testid="prototype-status-evidence">
          <span>Visual</span>
          <b>4 shots</b>
        </button>
        <button class="usage-pill" (click)="statusPanelRequested.emit('queue')">
          <span>Tools</span>
          <b>28</b>
        </button>
      </div>
      <div class="statusbar__group statusbar__group--right">
        <button class="statusbar__item statusbar__item--usage" (click)="statusPanelRequested.emit('tokens')" title="Codex and Claude 5-hour quota windows">
          <span>Usage</span>
          <b>{{ compactUsageSummary }}</b>
        </button>
        <button class="statusbar__item" (click)="sideSheetRequested.emit()">
          <span>Orch</span>
        </button>
        <button class="statusbar__item" (click)="debugTraceRequested.emit()">
          <span>Feed</span>
        </button>
        <button class="statusbar__item" (click)="statusPanelRequested.emit('evidence')">
          <span>Visual</span>
        </button>
        <span class="statusbar__sep"></span>
        <button class="statusbar__item statusbar__picker" (click)="statusPanelRequested.emit('model')" data-testid="prototype-status-model">
          <b>Codex</b>
          <span class="statusbar__caret">v</span>
        </button>
        <button class="statusbar__item statusbar__picker" (click)="statusPanelRequested.emit('model')">
          <b>5.5 Extra High</b>
          <span class="statusbar__caret">v</span>
        </button>
      </div>
    </footer>
  `,
  styles: [`
    :host {
      grid-column: 2;
      min-width: 0;
      display: block;
    }

    * { box-sizing: border-box; }
    button { font: inherit; cursor: pointer; }

    .statusbar {
      min-height: 28px;
      display: grid;
      grid-template-columns: minmax(230px, .75fr) minmax(460px, 1.25fr) auto;
      gap: 6px;
      align-items: center;
      background: #11111b;
      border-top: 1px solid rgba(255,255,255,0.08);
      color: rgba(255,255,255,0.76);
      padding: 0 8px;
      font-size: 11px;
      letter-spacing: 0;
    }

    .statusbar__group {
      min-width: 0;
      display: flex;
      align-items: center;
      gap: 3px;
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
      height: 22px;
      display: inline-flex;
      align-items: center;
      gap: 5px;
      border: 0;
      border-radius: 4px;
      background: transparent;
      color: inherit;
      padding: 0 7px;
      font-size: inherit;
      white-space: nowrap;
    }

    .statusbar button:hover {
      background: rgba(255,255,255,0.10);
      color: #fff;
    }

    .statusbar__item {
      color: rgba(255,255,255,0.76);
    }

    .statusbar__item--readonly {
      color: rgba(255,255,255,0.58);
    }

    .statusbar__dot {
      width: 7px;
      height: 7px;
      border-radius: 999px;
      background: #8bd17c;
      box-shadow: 0 0 0 2px rgba(139, 209, 124, .14);
    }

    .statusbar__dot--live {
      animation: statusbar-live 1.5s ease-in-out infinite;
    }

    @keyframes statusbar-live {
      0%, 100% { opacity: 1; }
      50% { opacity: .48; }
    }

    .statusbar__icon-text {
      color: rgba(255,255,255,0.50);
      font-weight: 750;
      text-transform: uppercase;
      font-size: 9px;
    }

    .statusbar__sep {
      width: 1px;
      height: 16px;
      margin: 0 3px;
      background: rgba(255,255,255,0.14);
      flex: 0 0 auto;
    }

    .statusbar__picker {
      border: 1px solid rgba(255,255,255,0.12) !important;
      background: rgba(255,255,255,0.04) !important;
    }

    .statusbar__picker b,
    .usage-pill b {
      color: #fff;
      font-weight: 760;
    }

    .statusbar__caret {
      color: rgba(255,255,255,0.48);
      font-size: 9px;
    }

    .statusbar__usage {
      gap: 4px;
    }

    .usage-pill {
      border: 1px solid rgba(255,255,255,0.10) !important;
      background: rgba(255,255,255,0.035) !important;
    }

    .usage-pill[data-tone="ok"] b { color: #a6d189; }
    .usage-pill[data-tone="warn"] b,
    .usage-pill--tokens b { color: #e6b673; }
    .usage-pill[data-tone="hot"] b { color: #f27d8a; }

    .usage-pill span:first-child {
      color: rgba(255,255,255,0.56);
    }

    .usage-pill small {
      color: rgba(255,255,255,0.46);
      font-size: 9px;
      font-weight: 740;
    }

    .statusbar__item--usage b {
      color: #e6b673;
      font-weight: 760;
    }

    @media (max-width: 720px) {
      .statusbar {
        grid-template-columns: minmax(0, 1fr) auto;
      }

      .statusbar__group--center {
        display: none;
      }

      .statusbar__group:first-child button:nth-child(n+2),
      .statusbar__group--right button:nth-child(2),
      .statusbar__group--right button:nth-child(3) {
        display: none;
      }

      .statusbar__group--right button:first-child {
        max-width: 172px;
      }

      .statusbar__group--right button:first-child span,
      .statusbar__group--right button:first-child b {
        min-width: 0;
        max-width: 92px;
        overflow: hidden;
        text-overflow: ellipsis;
      }
    }
  `],
})
export class FoundNextStatusbarComponent {
  readonly statusPanelRequested = output<StatusPanel>();
  readonly sideSheetRequested = output<void>();
  readonly debugTraceRequested = output<void>();

  readonly usageStrip = USAGE_STRIP;
  readonly compactUsageSummary = USAGE_STRIP
    .filter((item) => item.window === '5h')
    .map((item) => `${item.label === 'Codex' ? 'Cdx' : item.label.slice(0, 2)} ${item.value}`)
    .join(' / ');
}

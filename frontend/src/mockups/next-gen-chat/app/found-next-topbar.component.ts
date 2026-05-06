import { Component, input, output } from '@angular/core';

import { ICON_PATHS, PROJECT_TABS, TOPBAR_RUN_STATS } from './next-gen-chat-workbench-prototype.data';
import { Density, StatusPanel, Theme } from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'app-found-next-topbar',
  standalone: true,
  template: `
    <header class="topbar">
      <div class="topbar__title">
        <strong>Agent Software Studio</strong>
        <span>Task workbench</span>
      </div>
      <div class="topbar__projects" aria-label="Project filter" data-testid="prototype-project-switcher">
        @for (project of projectTabs; track project.name) {
          <button class="project-chip"
                  [class.project-chip--active]="project.active"
                  [style.--project-color]="project.color"
                  [style.--project-soft]="project.soft"
                  [style.--project-border]="project.border"
                  [style.--project-on]="project.on"
                  [attr.title]="project.tooltip"
                  (click)="projectPanelRequested.emit()">
            <span class="project-chip__disk">{{ project.initial }}</span>
            <span class="project-chip__name">{{ project.name }}</span>
            <span class="project-chip__auto">{{ project.auto }}</span>
          </button>
        }
      </div>
      <div class="topbar__runline" aria-label="Current run summary" data-testid="prototype-topbar-runline">
        @for (stat of runStats; track stat) {
          <span>{{ stat }}</span>
        }
      </div>
      <div class="topbar__actions">
        <button class="owner-switch"
                title="Owner filter: Robert"
                aria-label="Owner filter Robert"
                data-testid="prototype-owner-switch"
                (click)="projectPanelRequested.emit()">
          <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
            @for (path of iconPath('user'); track path) {
              <path [attr.d]="path"></path>
            }
          </svg>
          <span>Robert</span>
        </button>
        <button class="icon-btn"
                [class.icon-btn--active]="sideSheetOpen()"
                title="Toggle project sheet"
                aria-label="Toggle project sheet"
                (click)="sideSheetToggled.emit()"
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
                (click)="queuePanelRequested.emit()"
                data-testid="prototype-topbar-queue">
          <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
            @for (path of iconPath('columns'); track path) {
              <path [attr.d]="path"></path>
            }
          </svg>
        </button>
        <button class="icon-btn" title="Toggle density" aria-label="Toggle density" (click)="densityToggled.emit()" data-testid="prototype-density-toggle">
          <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
            @for (path of iconPath(density() === 'compact' ? 'expand' : 'compress'); track path) {
              <path [attr.d]="path"></path>
            }
          </svg>
        </button>
        <button class="icon-btn" title="Toggle theme" aria-label="Toggle theme" (click)="themeToggled.emit()" data-testid="prototype-theme-toggle">
          <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
            @for (path of iconPath(theme() === 'light' ? 'sun' : 'moon'); track path) {
              <path [attr.d]="path"></path>
            }
          </svg>
        </button>
        <button class="icon-btn" title="Command palette" aria-label="Command palette" (click)="commandRequested.emit()" data-testid="prototype-command-open">
          <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
            @for (path of iconPath('command'); track path) {
              <path [attr.d]="path"></path>
            }
          </svg>
        </button>
        <button class="icon-btn" title="Verbose debug" aria-label="Verbose debug" (click)="debugRequested.emit()" data-testid="prototype-debug-open">
          <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
            @for (path of iconPath('bug'); track path) {
              <path [attr.d]="path"></path>
            }
          </svg>
        </button>
        <button class="icon-btn" title="Close prototype" aria-label="Close prototype" (click)="closeRequested.emit()">
          <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
            @for (path of iconPath('close'); track path) {
              <path [attr.d]="path"></path>
            }
          </svg>
        </button>
      </div>
    </header>
  `,
  styles: [`
    :host {
      grid-column: 2;
      min-width: 0;
      display: block;
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

    .topbar {
      height: 32px;
      display: grid;
      grid-template-columns: minmax(148px, auto) minmax(320px, 1fr) auto auto;
      align-items: center;
      gap: 8px;
      padding: 0 7px 0 9px;
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

    .topbar__projects {
      min-width: 0;
      display: flex;
      align-items: center;
      gap: 5px;
      overflow: hidden;
    }

    .project-chip {
      min-width: 0;
      max-width: 176px;
      min-height: 22px;
      display: inline-flex;
      align-items: center;
      gap: 5px;
      border: 1px solid var(--project-border, var(--line));
      border-radius: 5px;
      background: color-mix(in srgb, var(--project-soft, var(--surface-soft)) 58%, var(--surface));
      color: var(--text);
      padding: 1px 6px 1px 4px;
      font-size: 11px;
      font-weight: 700;
    }

    .project-chip--active {
      background: var(--project-soft, var(--surface-soft));
      box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--project-border, var(--accent)) 48%, transparent);
    }

    .project-chip__disk {
      width: 17px;
      height: 17px;
      display: grid;
      place-items: center;
      border-radius: 5px;
      background: var(--project-color, var(--accent));
      color: var(--project-on, #fff);
      font-size: 10px;
      font-weight: 850;
      flex: 0 0 auto;
    }

    .project-chip__name {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .project-chip__auto {
      color: var(--muted);
      font-size: 10px;
      font-weight: 650;
      white-space: nowrap;
    }

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

    .topbar__actions {
      display: flex;
      align-items: center;
      gap: 4px;
    }

    .owner-switch {
      min-height: 26px;
      max-width: 108px;
      display: inline-flex;
      align-items: center;
      gap: 5px;
      border: 1px solid var(--line);
      border-radius: 7px;
      background: var(--surface);
      color: var(--text);
      padding: 0 8px;
      font-size: 11px;
      font-weight: 750;
      white-space: nowrap;
    }

    .owner-switch .svg-icon {
      width: 13px;
      height: 13px;
    }

    .icon-btn {
      width: 26px;
      height: 26px;
      border: 1px solid transparent;
      border-radius: 5px;
      display: grid;
      place-items: center;
      background: transparent;
      color: var(--muted);
      font-size: 12px;
      font-weight: 700;
    }

    .icon-btn .svg-icon {
      width: 15px;
      height: 15px;
    }

    .icon-btn:hover,
    .icon-btn--active {
      color: var(--text);
      background: var(--surface);
      border-color: var(--line);
    }

    @media (max-width: 720px) {
      :host {
        grid-column: 1;
      }

      .topbar {
        grid-template-columns: minmax(0, 1fr) auto auto;
        gap: 4px;
      }

      .topbar__title span,
      .topbar__projects,
      .topbar__runline span:nth-child(n+3),
      .owner-switch span,
      .topbar__actions button:nth-of-type(n+6) {
        display: none;
      }

      .owner-switch {
        width: 28px;
        min-width: 28px;
        padding: 0;
        justify-content: center;
      }
    }
  `],
})
export class FoundNextTopbarComponent {
  readonly theme = input.required<Theme>();
  readonly density = input.required<Density>();
  readonly sideSheetOpen = input.required<boolean>();
  readonly statusPanel = input<StatusPanel | null>(null);

  readonly projectPanelRequested = output<void>();
  readonly sideSheetToggled = output<void>();
  readonly queuePanelRequested = output<void>();
  readonly densityToggled = output<void>();
  readonly themeToggled = output<void>();
  readonly commandRequested = output<void>();
  readonly debugRequested = output<void>();
  readonly closeRequested = output<void>();

  readonly projectTabs = PROJECT_TABS;
  readonly runStats = TOPBAR_RUN_STATS;

  iconPath(name: string): string[] {
    return ICON_PATHS[name] ?? ICON_PATHS['help'];
  }
}

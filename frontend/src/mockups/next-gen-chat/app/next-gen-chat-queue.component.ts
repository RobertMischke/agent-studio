import { Component, input, output } from '@angular/core';

import { ICON_PATHS } from './next-gen-chat-workbench-prototype.data';
import { TaskQueueCard } from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'mockup-next-gen-chat-queue',
  standalone: true,
  template: `
    <aside class="task-list" aria-label="Queue module" data-testid="prototype-queue-module">
      <div class="task-list__header">
        <div>
          <span class="task-list__eyebrow">Tasks module</span>
          <strong>Queue</strong>
          <span>2-ready · 5 upcoming</span>
        </div>
        <button class="task-list__close icon-btn"
                type="button"
                title="Close Queue module"
                aria-label="Close Queue module"
                data-testid="prototype-queue-close"
                (click)="closeRequested.emit()">
          <svg class="svg-icon" viewBox="0 0 24 24" aria-hidden="true">
            @for (path of iconPath('panelClose'); track path) {
              <path [attr.d]="path"></path>
            }
          </svg>
        </button>
      </div>

      <div class="task-list__filters" aria-label="Queue filters">
        <button class="task-list__filter task-list__filter--active">Ready</button>
        <button class="task-list__filter">Review</button>
        <button class="task-list__filter">All</button>
      </div>

      @for (task of tasks(); track task.id) {
        <button class="task-card"
                [class.task-card--active]="task.active"
                [attr.data-state]="task.state">
          <span class="task-card__top">
            <b>{{ task.order }}</b>
            <em>{{ task.lane }}</em>
          </span>
          <span class="task-card__title">{{ task.title }}</span>
          <span class="task-card__meta">
            <span>{{ task.agent }}</span>
            <span>{{ task.meta }}</span>
          </span>
        </button>
      }
    </aside>
  `,
})
export class NextGenChatQueueComponent {
  readonly tasks = input.required<readonly TaskQueueCard[]>();
  readonly closeRequested = output<void>();

  iconPath(name: string): string[] {
    return ICON_PATHS[name] ?? ICON_PATHS['help'];
  }
}

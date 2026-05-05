import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  PROJECT_RAIL_ITEMS,
  ProjectRailGroup,
  ProjectRailItem,
  ProjectRailKey,
} from './project-shell.config';

/**
 * Project page shell. Slice 2 of the quality-system mockup: introduces the
 * left-rail navigation skeleton plus a placeholder body per rail item.
 *
 * The shell is action-driven by design (see docs/mockups/quality-system/
 * README.md): mounting a panel must not run any analysis. Real content for
 * each rail item lands in a separate follow-up slice.
 */
@Component({
  selector: 'app-project-shell',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="proj-shell" data-testid="project-shell">
      <header class="proj-shell__head">
        <div class="proj-shell__title-row">
          <button class="proj-shell__back"
                  type="button"
                  data-testid="project-shell-back"
                  (click)="closeShell.emit()">← Board</button>
          <h1 class="proj-shell__title" data-testid="project-shell-title">{{ projectName() }}</h1>
          <span class="proj-shell__chip" data-testid="project-shell-chip">this repo</span>
          <span class="proj-shell__spacer"></span>
          <button class="proj-shell__feed"
                  type="button"
                  data-testid="project-shell-open-feed"
                  (click)="openFeed.emit()">📜 Open feed</button>
        </div>
      </header>

      <div class="proj-shell__body">
        <aside class="proj-shell__rail" data-testid="project-shell-rail" aria-label="Project sections">
          @for (group of railGroups(); track group.id) {
            <div class="proj-shell__rail-group">{{ group.label }}</div>
            @for (item of group.items; track item.key) {
              <button class="proj-shell__rail-item"
                      type="button"
                      [class.proj-shell__rail-item--active]="item.key === activeRail()"
                      [attr.data-testid]="'project-shell-rail-' + item.key"
                      [attr.aria-current]="item.key === activeRail() ? 'page' : null"
                      (click)="onRailClick(item.key)">
                <span class="proj-shell__rail-icon" aria-hidden="true">{{ item.icon }}</span>
                <span class="proj-shell__rail-label">{{ item.label }}</span>
              </button>
            }
          }
        </aside>

        <main class="proj-shell__panel"
              [attr.data-testid]="'project-shell-panel-' + activeRail()"
              [attr.data-rail-key]="activeRail()">
          @if (hasCustomPanel()) {
            <ng-content></ng-content>
          } @else {
            <header class="proj-shell__panel-head">
              <h2 class="proj-shell__panel-title" data-testid="project-shell-panel-title">
                <span aria-hidden="true">{{ activeItem().icon }}</span>
                {{ activeItem().panelTitle }}
              </h2>
              <p class="proj-shell__panel-desc" data-testid="project-shell-panel-desc">
                {{ activeItem().description }}
              </p>
            </header>
            <div class="proj-shell__empty" data-testid="project-shell-panel-empty">
              <p>{{ activeItem().empty }}</p>
            </div>
          }
        </main>
      </div>
    </section>
  `,
  styles: [`
    :host { display: block; height: 100%; overflow: hidden; }

    .proj-shell {
      display: flex;
      flex-direction: column;
      height: 100%;
      background: #181825;
      color: #cdd6f4;
    }

    .proj-shell__head {
      padding: 14px 22px;
      border-bottom: 1px solid #313244;
      background: #1e1e2e;
    }
    .proj-shell__title-row {
      display: flex;
      align-items: center;
      gap: 10px;
      flex-wrap: wrap;
    }
    .proj-shell__back {
      background: transparent;
      color: #a6adc8;
      border: 1px solid transparent;
      padding: 4px 10px;
      border-radius: 6px;
      cursor: pointer;
      font: inherit;
      font-size: 0.82rem;
    }
    .proj-shell__back:hover { background: #313244; color: #cdd6f4; }
    .proj-shell__title { margin: 0; font-size: 1.15rem; color: #f8fafc; font-weight: 600; }
    .proj-shell__chip {
      font-size: 0.72rem;
      letter-spacing: 0.02em;
      padding: 2px 8px;
      border-radius: 999px;
      background: #313244;
      color: #bac2de;
    }
    .proj-shell__spacer { flex: 1; }
    .proj-shell__feed {
      background: rgba(249, 226, 175, 0.14);
      color: #fcd34d;
      border: 1px solid rgba(249, 226, 175, 0.40);
      padding: 5px 12px;
      border-radius: 6px;
      cursor: pointer;
      font: inherit;
      font-size: 0.80rem;
    }
    .proj-shell__feed:hover { background: rgba(249, 226, 175, 0.22); }

    .proj-shell__body {
      display: grid;
      grid-template-columns: 220px 1fr;
      gap: 22px;
      padding: 22px 26px 32px;
      overflow: auto;
      flex: 1;
      min-height: 0;
    }

    .proj-shell__rail {
      background: #1e1e2e;
      border: 1px solid #313244;
      border-radius: 8px;
      padding: 10px;
      align-self: start;
      position: sticky;
      top: 0;
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .proj-shell__rail-group {
      color: #6c7086;
      font-size: 0.68rem;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      margin: 8px 8px 4px;
    }
    .proj-shell__rail-group:first-child { margin-top: 4px; }
    .proj-shell__rail-item {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 6px 10px;
      border-radius: 6px;
      border: none;
      background: transparent;
      color: #bac2de;
      cursor: pointer;
      text-align: left;
      font: inherit;
      font-size: 0.85rem;
    }
    .proj-shell__rail-item:hover { background: #313244; color: #cdd6f4; }
    .proj-shell__rail-item--active {
      background: #313244;
      color: #f8fafc;
      font-weight: 600;
    }
    .proj-shell__rail-icon { width: 18px; text-align: center; }

    .proj-shell__panel {
      background: #1e1e2e;
      border: 1px solid #313244;
      border-radius: 8px;
      padding: 22px 24px;
      min-height: 240px;
    }
    .proj-shell__panel-head {
      padding-bottom: 12px;
      border-bottom: 1px solid #313244;
      margin-bottom: 16px;
    }
    .proj-shell__panel-title {
      margin: 0 0 4px;
      font-size: 1.05rem;
      font-weight: 600;
      color: #f8fafc;
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .proj-shell__panel-desc {
      margin: 0;
      color: #a6adc8;
      font-size: 0.85rem;
    }
    .proj-shell__empty {
      padding: 32px 20px;
      text-align: center;
      color: #6c7086;
      font-size: 0.88rem;
      border: 1px dashed #313244;
      border-radius: 6px;
      background: rgba(0,0,0,0.18);
    }
    .proj-shell__empty p { margin: 0; }

    @media (max-width: 720px) {
      .proj-shell__body { grid-template-columns: 1fr; }
      .proj-shell__rail { position: static; }
    }
  `],
})
export class ProjectShellComponent {
  readonly projectName = input.required<string>();
  readonly activeRail = input.required<ProjectRailKey>();
  /**
   * When true, the shell hides its built-in panel header + empty state and
   * surfaces an `<ng-content>` slot in the panel body instead. The host
   * (typically `app.ts`) projects a real panel component (Security panel,
   * etc.) and owns the panel header so the slice can paint its own
   * baseline badge / cards / actions per the mockup. When false, the
   * generic placeholder + description still renders for rails that have
   * not landed their content slice yet.
   */
  readonly hasCustomPanel = input<boolean>(false);
  readonly railChange = output<ProjectRailKey>();
  readonly openFeed = output<void>();
  readonly closeShell = output<void>();

  /** Rail items grouped for the side nav (PROJECT / CONFIGURATION). */
  readonly railGroups = computed<readonly { id: ProjectRailGroup; label: string; items: readonly ProjectRailItem[] }[]>(() => {
    const groups: { id: ProjectRailGroup; label: string; items: ProjectRailItem[] }[] = [
      { id: 'project', label: 'Project', items: [] },
      { id: 'configuration', label: 'Configuration', items: [] },
    ];
    for (const item of PROJECT_RAIL_ITEMS) {
      const bucket = groups.find(g => g.id === item.group);
      bucket?.items.push(item);
    }
    return groups;
  });

  /** Panel descriptor for the currently selected rail key. */
  readonly activeItem = computed<ProjectRailItem>(() => {
    const key = this.activeRail();
    return PROJECT_RAIL_ITEMS.find(i => i.key === key) ?? PROJECT_RAIL_ITEMS[0];
  });

  onRailClick(key: ProjectRailKey): void {
    if (key === this.activeRail()) return;
    this.railChange.emit(key);
  }
}

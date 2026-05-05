import { Injectable, signal } from '@angular/core';

/**
 * Frontend-only feature flags persisted in localStorage. Each flag toggles
 * an experimental UI surface that ships behind it; default off.
 *
 * Flags here are not user-facing settings. They are dev/QA hooks for in-flight
 * UI redesigns. Tests flip them via `localStorage` before navigating, the way
 * Playwright already does for other persisted preferences.
 *
 * To enable from the browser console:
 *
 *   localStorage.setItem('atp.flag.vsCodeLayout', '1'); location.reload();
 *   localStorage.setItem('atp.flag.nextGenChat', '1'); location.reload();
 */
@Injectable({ providedIn: 'root' })
export class FeatureFlagsService {
  private static readonly KEY_VS_CODE_LAYOUT = 'atp.flag.vsCodeLayout';
  private static readonly KEY_VS_CODE_META_OPEN = 'atp.flag.vsCodeLayout.metaOpen';
  private static readonly KEY_KANBAN_DESIGN_SPEC_V1 = 'atp.flag.kanbanDesignSpecV1';
  private static readonly KEY_NEXT_GEN_CHAT = 'atp.flag.nextGenChat';

  /** `Frontend:VsCodeLayout` — VS Code-style chrome with status bar + collapsible meta. */
  readonly vsCodeLayout = signal<boolean>(this.read(FeatureFlagsService.KEY_VS_CODE_LAYOUT));

  /** Meta-pane open state for the VS Code layout. Persisted independently. */
  readonly vsCodeMetaOpen = signal<boolean>(this.read(FeatureFlagsService.KEY_VS_CODE_META_OPEN));

  /**
   * `Frontend:KanbanDesignSpecV1` — slice 1 of the Kanban board design
   * spec at docs/mockups/kanban-board-design/. Lands the locked grid
   * template and the spacing/sizing rhythm. Off keeps the legacy flex
   * layout untouched.
   */
  readonly kanbanDesignSpecV1 = signal<boolean>(this.read(FeatureFlagsService.KEY_KANBAN_DESIGN_SPEC_V1));

  /**
   * `Frontend:NextGenChat` - production rollout flag for the next-gen chat
   * conversation grammar inside the existing task Activity tab and project
   * side sheet. Independent from `Frontend:VsCodeLayout`. Default off; when
   * off every host must render exactly as before. The flag exists so the
   * shared `ConversationEvent` projection can land before any visible
   * replacement renderer is wired into a host.
   *
   * See docs/mockups/chat-window-next-gen/integration-plan.md and
   * docs/mockups/chat-window-next-gen/host-inventory.md.
   */
  readonly nextGenChat = signal<boolean>(this.read(FeatureFlagsService.KEY_NEXT_GEN_CHAT));

  setVsCodeLayout(on: boolean): void {
    this.vsCodeLayout.set(on);
    this.write(FeatureFlagsService.KEY_VS_CODE_LAYOUT, on);
  }

  setVsCodeMetaOpen(open: boolean): void {
    this.vsCodeMetaOpen.set(open);
    this.write(FeatureFlagsService.KEY_VS_CODE_META_OPEN, open);
  }

  setKanbanDesignSpecV1(on: boolean): void {
    this.kanbanDesignSpecV1.set(on);
    this.write(FeatureFlagsService.KEY_KANBAN_DESIGN_SPEC_V1, on);
  }

  setNextGenChat(on: boolean): void {
    this.nextGenChat.set(on);
    this.write(FeatureFlagsService.KEY_NEXT_GEN_CHAT, on);
  }

  private read(key: string): boolean {
    if (typeof window === 'undefined') return false;
    try {
      return window.localStorage?.getItem(key) === '1';
    } catch {
      return false;
    }
  }

  private write(key: string, value: boolean): void {
    if (typeof window === 'undefined') return;
    try {
      if (value) window.localStorage?.setItem(key, '1');
      else window.localStorage?.removeItem(key);
    } catch {
      /* storage may be blocked; signal still reflects the live value */
    }
  }
}

import { Injectable, signal } from '@angular/core';

/**
 * Frontend-only feature flags persisted in localStorage. Most flags ship
 * default-off; `vsCodeLayout` is the exception — it ships default-on now
 * that the Agent Software Studio shell migration completed (titlebar,
 * activity bar, sidebar panels, tab host, chat rail, hub + diff +
 * activity tabs). Set the storage key to '0' to opt back into the
 * legacy chrome.
 *
 * Flags here are not user-facing settings. They are dev/QA hooks for in-flight
 * UI redesigns. Tests flip them via `localStorage` before navigating, the way
 * Playwright already does for other persisted preferences.
 *
 * To opt back into the legacy chrome from the browser console:
 *
 *   localStorage.setItem('atp.flag.vsCodeLayout', '0'); location.reload();
 *
 * To enable other flags:
 *
 *   localStorage.setItem('atp.flag.nextGenChat', '1'); location.reload();
 */
@Injectable({ providedIn: 'root' })
export class FeatureFlagsService {
  private static readonly KEY_VS_CODE_LAYOUT = 'atp.flag.vsCodeLayout';
  private static readonly KEY_VS_CODE_META_OPEN = 'atp.flag.vsCodeLayout.metaOpen';
  private static readonly KEY_KANBAN_DESIGN_SPEC_V1 = 'atp.flag.kanbanDesignSpecV1';
  private static readonly KEY_NEXT_GEN_CHAT = 'atp.flag.nextGenChat';

  /**
   * `Frontend:VsCodeLayout` — Agent Software Studio shell.
   * Default ON: absence of the storage key counts as opt-in.
   * The user can set the key to '0' to fall back to the legacy chrome.
   */
  readonly vsCodeLayout = signal<boolean>(this.readWithDefault(FeatureFlagsService.KEY_VS_CODE_LAYOUT, true));

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
    // Default is ON now, so off must be written explicitly as '0' instead of
    // removing the key (a missing key is read as "use the default").
    this.writeExplicit(FeatureFlagsService.KEY_VS_CODE_LAYOUT, on);
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

  /**
   * Read a flag with an explicit default for the "key absent" case.
   * Treats '1' as on, '0' as explicit off, and missing key as the
   * caller-supplied default.
   */
  private readWithDefault(key: string, defaultValue: boolean): boolean {
    if (typeof window === 'undefined') return defaultValue;
    try {
      const raw = window.localStorage?.getItem(key);
      if (raw === '1') return true;
      if (raw === '0') return false;
      return defaultValue;
    } catch {
      return defaultValue;
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

  /**
   * Like {@link write} but writes '0' for off instead of removing the key.
   * Used by flags whose default is ON: if we removed the key on off,
   * the next reload would read the default and silently re-enable the
   * flag the user just opted out of.
   */
  private writeExplicit(key: string, value: boolean): void {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage?.setItem(key, value ? '1' : '0');
    } catch {
      /* storage may be blocked; signal still reflects the live value */
    }
  }
}

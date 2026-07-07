import { Injectable, computed, signal } from '@angular/core';

/**
 * Sections of the global Workspace-settings home ("Dach"). Several sections
 * were previously independent, scattered overlays (token timeline,
 * visual-evidence reel, executive summary, CLI usage caps). ASS-695 folds
 * those into one rail+panel home so they are centrally findable instead of
 * strewn across the status bar, mirroring the project-level settings layout.
 * `overview` is the landing rail item that links into each section.
 */
export type WorkspaceSettingsSection =
  | 'overview'
  | 'caps'
  | 'prompts'
  | 'tokens'
  | 'screenshots'
  | 'summary';

/**
 * Shell-feature service: open/close state + URL-hash sync for the global
 * Workspace-settings home. One modal, one open flag, one active section.
 *
 * The home is deep-linkable so a bookmark or copy-pasted address
 * reproduces the open section. The legacy per-overlay hashes
 * (`#/workspace/tokens`, `#/workspace/screenshots`, `#/workspace/summary`
 * and its `#/summary` alias) are preserved so existing links and specs
 * keep resolving to the right section.
 *
 * Lifted out of `app.ts` per ADR-0034 so the shell is a thin coordinator
 * and the overlay state machine has a single grep target. Rendering lives
 * in `WorkspaceOverlaysComponent`; the shell just mounts that component
 * once.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceOverlaysService {
  /** Whether the global Workspace-settings home is open. */
  readonly settingsOpen = signal<boolean>(false);
  /** The active section inside the home. */
  readonly section = signal<WorkspaceSettingsSection>('overview');

  /**
   * Back-compat read signals. Each loose overlay is now a section of the
   * one home; the shell and existing template guards keep reading these
   * without caring that the underlying state collapsed into one modal.
   */
  readonly tokensOpen = computed(() => this.settingsOpen() && this.section() === 'tokens');
  readonly screenshotsOpen = computed(() => this.settingsOpen() && this.section() === 'screenshots');
  readonly summaryOpen = computed(() => this.settingsOpen() && this.section() === 'summary');
  readonly cliAdminOpen = computed(() => this.settingsOpen() && this.section() === 'caps');
  readonly promptAdminOpen = computed(() => this.settingsOpen() && this.section() === 'prompts');

  /** True iff the home is open in any section. */
  readonly anyOpen = computed(() => this.settingsOpen());

  /**
   * True iff the home is open on a section OTHER than 'caps'. The 'caps'
   * (CLI usage) section has its own dedicated "Usage" status-bar pill, so
   * the "Settings" pill must not also light up while Usage is showing —
   * otherwise both pills carry the single `--studio-accent` active fill at
   * once (see docs/frontend/design-system.md, "one accent per rail"). This
   * is the Settings pill's `active` source; `anyOpen` stays the general
   * "is the home open at all" read for everything else.
   */
  readonly anyOpenExceptUsage = computed(() => this.settingsOpen() && this.section() !== 'caps');

  /**
   * True when the current visible state was reached by a deep-link hash,
   * so a back/forward navigation that drops the hash closes the home.
   * Button-triggered opens survive unrelated hash churn (mirrors the old
   * menu-only CLI-admin overlay, which carried no URL contract).
   */
  private openedViaHash = false;

  // ---------- generic home control ----------

  open(section: WorkspaceSettingsSection): void {
    this.section.set(section);
    this.settingsOpen.set(true);
    this.openedViaHash = false;
    this.writeHash(this.hashForSection(section));
  }

  /** Switch the active section, opening the home first if it is closed. */
  select(section: WorkspaceSettingsSection): void {
    if (!this.settingsOpen()) {
      this.open(section);
      return;
    }
    if (this.section() === section) return;
    this.section.set(section);
    this.writeHash(this.hashForSection(section));
  }

  close(): void {
    if (!this.settingsOpen()) return;
    this.settingsOpen.set(false);
    this.openedViaHash = false;
    this.clearOwnHash();
  }

  toggle(section: WorkspaceSettingsSection): void {
    if (this.settingsOpen() && this.section() === section) this.close();
    else this.open(section);
  }

  // ---------- home landing ----------

  openSettings(): void { this.open('overview'); }
  toggleSettings(): void { this.toggle('overview'); }

  // ---------- back-compat wrappers ----------
  // External call sites (status bar, usage hover panel, studio shell,
  // screenshot reel) still call these; they now route into the home.

  openTokens(): void { this.open('tokens'); }
  closeTokens(): void { this.close(); }
  toggleTokens(): void { this.toggle('tokens'); }

  openScreenshots(): void { this.open('screenshots'); }
  closeScreenshots(): void { this.close(); }
  toggleScreenshots(): void { this.toggle('screenshots'); }

  openSummary(): void { this.open('summary'); }
  closeSummary(): void { this.close(); }
  toggleSummary(): void { this.toggle('summary'); }

  openCliAdmin(): void { this.open('caps'); }
  closeCliAdmin(): void { this.close(); }
  toggleCliAdmin(): void { this.toggle('caps'); }

  openPromptAdmin(): void { this.open('prompts'); }
  closePromptAdmin(): void { this.close(); }
  togglePromptAdmin(): void { this.toggle('prompts'); }

  /**
   * Reconcile open state with the current URL hash. Call once on app boot
   * and on every `hashchange` event. A recognised section hash opens (or
   * switches) the home; dropping a hash that opened the home closes it.
   * Project-shell hash sync is separate and lives on the shell component.
   */
  syncFromHash(): void {
    const section = this.sectionForHash(window.location.hash);
    if (section) {
      if (this.section() !== section) this.section.set(section);
      if (!this.settingsOpen()) this.settingsOpen.set(true);
      this.openedViaHash = true;
    } else if (this.settingsOpen() && this.openedViaHash) {
      this.settingsOpen.set(false);
      this.openedViaHash = false;
    }
  }

  private sectionForHash(hash: string): WorkspaceSettingsSection | null {
    switch (hash) {
      case '#/workspace/tokens': return 'tokens';
      case '#/workspace/screenshots': return 'screenshots';
      case '#/workspace/summary':
      case '#/summary': return 'summary';
      case '#/workspace/settings/caps':
      case '#/workspace/caps': return 'caps';
      case '#/workspace/settings/prompts':
      case '#/workspace/prompts': return 'prompts';
      case '#/workspace/settings': return 'overview';
      default: return null;
    }
  }

  private hashForSection(section: WorkspaceSettingsSection): string {
    switch (section) {
      case 'tokens': return '#/workspace/tokens';
      case 'screenshots': return '#/workspace/screenshots';
      case 'summary': return '#/workspace/summary';
      case 'caps': return '#/workspace/settings/caps';
      case 'prompts': return '#/workspace/settings/prompts';
      case 'overview': return '#/workspace/settings';
    }
  }

  private readonly ownHashes = new Set<string>([
    '#/workspace/settings',
    '#/workspace/settings/caps',
    '#/workspace/settings/prompts',
    '#/workspace/caps',
    '#/workspace/prompts',
    '#/workspace/tokens',
    '#/workspace/screenshots',
    '#/workspace/summary',
    '#/summary',
  ]);

  private writeHash(target: string): void {
    if (window.location.hash !== target) {
      try {
        history.replaceState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }

  private clearOwnHash(): void {
    if (this.ownHashes.has(window.location.hash)) {
      try {
        history.replaceState(null, '', window.location.pathname + window.location.search);
      } catch { /* ignore */ }
    }
  }
}

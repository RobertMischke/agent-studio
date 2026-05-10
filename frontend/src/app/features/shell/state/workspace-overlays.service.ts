import { Injectable, computed, signal } from '@angular/core';

/**
 * Cycle 9g shell-feature service: open/close state + URL-hash sync for
 * the three workspace-level overlays that live above the kanban shell:
 *
 *   - workspace-tokens          (`#/workspace/tokens`)        — token timeline overlay
 *   - workspace-screenshots     (`#/workspace/screenshots`)   — visual evidence reel
 *   - cli-admin                  (no hash; menu-triggered)    — CLI usage caps + admin
 *
 * The first two are deep-linkable so a bookmark or copy-pasted address
 * reproduces the open state; the CLI admin overlay is an in-app affordance
 * with no URL contract.
 *
 * Lifted out of `app.ts` per ADR-0034 so the shell is a thin coordinator
 * and the overlay state machine has a single grep target. The rendering
 * lives in `WorkspaceOverlaysComponent`; the shell just mounts that
 * component once.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceOverlaysService {
  readonly tokensOpen = signal<boolean>(false);
  readonly screenshotsOpen = signal<boolean>(false);
  readonly cliAdminOpen = signal<boolean>(false);

  /** True iff any of the three overlays is currently visible. */
  readonly anyOpen = computed(() =>
    this.tokensOpen() || this.screenshotsOpen() || this.cliAdminOpen());

  private readonly tokensHash = '#/workspace/tokens';
  private readonly screenshotsHash = '#/workspace/screenshots';

  // ---------- tokens ----------

  openTokens(): void {
    this.tokensOpen.set(true);
    this.writeHash(this.tokensHash);
  }

  closeTokens(): void {
    this.tokensOpen.set(false);
    this.clearHashIf(this.tokensHash);
  }

  toggleTokens(): void {
    if (this.tokensOpen()) this.closeTokens();
    else this.openTokens();
  }

  // ---------- screenshots ----------

  openScreenshots(): void {
    this.screenshotsOpen.set(true);
    this.writeHash(this.screenshotsHash);
  }

  closeScreenshots(): void {
    this.screenshotsOpen.set(false);
    this.clearHashIf(this.screenshotsHash);
  }

  toggleScreenshots(): void {
    if (this.screenshotsOpen()) this.closeScreenshots();
    else this.openScreenshots();
  }

  // ---------- cli-admin ----------

  openCliAdmin(): void { this.cliAdminOpen.set(true); }
  closeCliAdmin(): void { this.cliAdminOpen.set(false); }
  toggleCliAdmin(): void {
    if (this.cliAdminOpen()) this.closeCliAdmin();
    else this.openCliAdmin();
  }

  /**
   * Reconcile open state with the current URL hash. Call once on app
   * boot and on every `hashchange` event. Project-shell hash sync is
   * separate and lives on the shell component.
   */
  syncFromHash(): void {
    const hash = window.location.hash;
    const tokens = hash === this.tokensHash;
    if (tokens !== this.tokensOpen()) this.tokensOpen.set(tokens);
    const screenshots = hash === this.screenshotsHash;
    if (screenshots !== this.screenshotsOpen()) this.screenshotsOpen.set(screenshots);
  }

  private writeHash(target: string): void {
    if (window.location.hash !== target) {
      try {
        history.replaceState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }

  private clearHashIf(target: string): void {
    if (window.location.hash === target) {
      try {
        history.replaceState(null, '', window.location.pathname + window.location.search);
      } catch { /* ignore */ }
    }
  }
}

import { Injectable, signal } from '@angular/core';

export type WikiMetaSectionId =
  | 'pageInfo'
  | 'classification'
  | 'linkedElements'
  | 'history'
  | 'driftMetadata'
  | 'rootFolder'
  | 'driftControl';

interface WikiMetaPanelPersistedState {
  collapsed?: boolean;
  sections?: Partial<Record<WikiMetaSectionId, boolean>>;
}

const STORAGE_KEY = 'atp.wikiMetaPanel.v1';
const SECTION_IDS: readonly WikiMetaSectionId[] = [
  'pageInfo',
  'classification',
  'linkedElements',
  'history',
  'driftMetadata',
  'rootFolder',
  'driftControl',
];
const DEFAULT_COLLAPSED_SECTIONS = new Set<WikiMetaSectionId>(['history']);

/**
 * Browser-local presentation state for the wiki meta rail.
 *
 * The key used to contain only the strings `expanded` or `collapsed`. Reads
 * keep accepting those values so existing sessions retain their panel choice;
 * the next interaction writes the richer JSON shape with per-section state.
 */
@Injectable()
export class WikiMetaPanelStateService {
  readonly collapsed = signal(false);
  private readonly collapsedSections = signal<ReadonlySet<WikiMetaSectionId>>(
    new Set(DEFAULT_COLLAPSED_SECTIONS),
  );

  restore(): void {
    const defaults = new Set(DEFAULT_COLLAPSED_SECTIONS);
    try {
      const raw = globalThis.localStorage?.getItem(STORAGE_KEY);
      if (raw === 'collapsed' || raw === 'expanded') {
        this.collapsed.set(raw === 'collapsed');
        this.collapsedSections.set(defaults);
        return;
      }
      if (!raw) {
        this.collapsed.set(false);
        this.collapsedSections.set(defaults);
        return;
      }
      const parsed = JSON.parse(raw) as WikiMetaPanelPersistedState;
      this.collapsed.set(parsed.collapsed === true);
      for (const id of SECTION_IDS) {
        const stored = parsed.sections?.[id];
        if (stored === true) defaults.add(id);
        if (stored === false) defaults.delete(id);
      }
      this.collapsedSections.set(defaults);
    } catch {
      this.collapsed.set(false);
      this.collapsedSections.set(defaults);
    }
  }

  togglePanel(): void {
    this.collapsed.update(value => !value);
    this.persist();
  }

  setPanelCollapsed(collapsed: boolean): void {
    this.collapsed.set(collapsed);
    this.persist();
  }

  isSectionCollapsed(id: WikiMetaSectionId): boolean {
    return this.collapsedSections().has(id);
  }

  toggleSection(id: WikiMetaSectionId): void {
    this.collapsedSections.update(current => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
    this.persist();
  }

  private persist(): void {
    const sections = Object.fromEntries(
      SECTION_IDS.map(id => [id, this.isSectionCollapsed(id)]),
    ) as Record<WikiMetaSectionId, boolean>;
    try {
      globalThis.localStorage?.setItem(STORAGE_KEY, JSON.stringify({
        collapsed: this.collapsed(),
        sections,
      } satisfies WikiMetaPanelPersistedState));
    } catch {
      /* Browser storage is optional; interaction remains available in memory. */
    }
  }
}

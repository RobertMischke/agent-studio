import { Injectable, signal } from '@angular/core';

/** One starred wiki document of a project. */
export interface WikiStarEntry {
  relPath: string;
  /** Display label captured at the moment of starring (the page title then). */
  label: string;
  /** ISO timestamp of the starring moment; newest-first is the display order. */
  starredAt: string;
}

/**
 * localStorage key prefix in the style of the section's `atp.projectWiki.v1.*`
 * persistence; one key per project (`atp.projectWikiStars.v1.<project>`), the
 * value is the JSON array of {@link WikiStarEntry}.
 */
const WIKI_STARS_STORAGE_PREFIX = 'atp.projectWikiStars.v1.';

/**
 * Signal-backed star/favourite store for wiki documents. All stored projects
 * are hydrated once at construction, every mutation updates the signal and
 * writes through to localStorage, so every view reading `entries`/`isStarred`
 * inside a reactive context reacts live to a toggle anywhere in the wiki.
 */
@Injectable({ providedIn: 'root' })
export class WikiStarsService {
  private readonly state = signal<Record<string, readonly WikiStarEntry[]>>(readAllStoredStars());

  /** Live star list of a project, most recently starred first. */
  entries(projectName: string): readonly WikiStarEntry[] {
    return this.state()[projectName] ?? [];
  }

  isStarred(projectName: string, relPath: string): boolean {
    return this.entries(projectName).some(entry => entry.relPath === relPath);
  }

  toggle(projectName: string, relPath: string, label: string): void {
    if (this.isStarred(projectName, relPath)) this.unstar(projectName, relPath);
    else this.star(projectName, relPath, label);
  }

  star(projectName: string, relPath: string, label: string): void {
    if (!projectName || !relPath || this.isStarred(projectName, relPath)) return;
    const entry: WikiStarEntry = { relPath, label, starredAt: new Date().toISOString() };
    // Prepend so ties on `starredAt` (fast toggles) still list newest first.
    this.write(projectName, [entry, ...this.entries(projectName)]);
  }

  unstar(projectName: string, relPath: string): void {
    const current = this.entries(projectName);
    const next = current.filter(entry => entry.relPath !== relPath);
    if (next.length === current.length) return;
    this.write(projectName, next);
  }

  /**
   * Drop the star for a path and, when it is a folder, every star beneath it.
   * Called when a page or category is deleted so the favourites list never
   * points at a path that no longer exists (a "dead" star on the landing).
   */
  removeUnder(projectName: string, relPath: string): void {
    if (!projectName || !relPath) return;
    const prefix = `${relPath}/`;
    const current = this.entries(projectName);
    const next = current.filter(
      entry => entry.relPath !== relPath && !entry.relPath.startsWith(prefix),
    );
    if (next.length === current.length) return;
    this.write(projectName, next);
  }

  private write(projectName: string, entries: readonly WikiStarEntry[]): void {
    this.state.update(map => ({ ...map, [projectName]: entries }));
    try {
      const storage = globalThis.localStorage;
      if (!storage) return;
      const key = `${WIKI_STARS_STORAGE_PREFIX}${encodeURIComponent(projectName)}`;
      if (entries.length === 0) storage.removeItem(key);
      else storage.setItem(key, JSON.stringify(entries));
    } catch {
      /* persistence is a convenience; starring keeps working without storage */
    }
  }
}

function readAllStoredStars(): Record<string, readonly WikiStarEntry[]> {
  const map: Record<string, readonly WikiStarEntry[]> = {};
  try {
    const storage = globalThis.localStorage;
    if (!storage) return map;
    for (let i = 0; i < storage.length; i++) {
      const key = storage.key(i);
      if (!key?.startsWith(WIKI_STARS_STORAGE_PREFIX)) continue;
      const projectName = decodeURIComponent(key.slice(WIKI_STARS_STORAGE_PREFIX.length));
      const entries = parseEntries(storage.getItem(key));
      if (projectName && entries.length > 0) map[projectName] = entries;
    }
  } catch {
    /* storage unavailable: stars simply start empty */
  }
  return map;
}

function parseEntries(raw: string | null): readonly WikiStarEntry[] {
  if (!raw) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed
      .filter(isWikiStarEntry)
      .map(entry => ({ relPath: entry.relPath, label: entry.label, starredAt: entry.starredAt }))
      // Defensive newest-first sort; ISO timestamps compare lexicographically.
      .sort((a, b) => b.starredAt.localeCompare(a.starredAt));
  } catch {
    return [];
  }
}

function isWikiStarEntry(value: unknown): value is WikiStarEntry {
  const entry = value as Partial<WikiStarEntry> | null;
  return !!entry && typeof entry === 'object'
    && typeof entry.relPath === 'string' && entry.relPath.trim().length > 0
    && typeof entry.label === 'string'
    && typeof entry.starredAt === 'string';
}

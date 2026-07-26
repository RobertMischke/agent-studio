import { routeSegmentOf, withRouteSegment } from '../../../services/url-hash.util';
import {
  DEFAULT_PROJECT_RAIL_KEY,
  isProjectRailKey,
  type ProjectRailKey,
  toProjectSlug,
} from '../components/project-shell/project-shell.config';

export interface ProjectHubRouteProject {
  /** Immutable registry identity, for example `PROJ-002`. */
  id: string;
  displayName: string;
}

export interface ProjectHubRouteTarget {
  project: ProjectHubRouteProject;
  section: ProjectRailKey;
  /** Name-derived routes remain readable, but writers always emit the id. */
  legacySlug: boolean;
  /** Rail-owned query such as the Wiki's `?page=` target. */
  query: string;
}

const PROJECTS_PREFIX = '/projects/';

/** Canonical route segment for a Project Hub rail. */
export function projectHubRoute(projectId: string, section: ProjectRailKey): string {
  const id = encodeURIComponent(projectId.trim().toUpperCase());
  return `${PROJECTS_PREFIX}${id}`
    + (section === DEFAULT_PROJECT_RAIL_KEY ? '' : `/${section}`);
}

/**
 * Resolve a Project Hub hash through immutable registry ids. The former
 * display-name slug remains an input-only compatibility alias so bookmarks
 * created before the stable-id contract continue to work.
 */
export function parseProjectHubRoute(
  hash: string,
  projects: readonly ProjectHubRouteProject[],
): ProjectHubRouteTarget | null {
  const route = routeSegmentOf(hash);
  if (!route?.startsWith(PROJECTS_PREFIX)) return null;

  const queryIndex = route.indexOf('?');
  const path = queryIndex >= 0 ? route.slice(0, queryIndex) : route;
  const query = queryIndex >= 0 ? route.slice(queryIndex) : '';
  const parts = path.slice(PROJECTS_PREFIX.length).split('/');
  if (parts.length < 1 || parts.length > 2 || !parts[0]) return null;

  let reference: string;
  try {
    reference = decodeURIComponent(parts[0]);
  } catch {
    return null;
  }

  const byId = projects.find(project => project.id.toLowerCase() === reference.toLowerCase());
  const project = byId
    ?? projects.find(candidate => toProjectSlug(candidate.displayName) === reference.toLowerCase());
  if (!project) return null;

  const rawSection = parts[1] || DEFAULT_PROJECT_RAIL_KEY;
  if (!isProjectRailKey(rawSection)) return null;
  return {
    project,
    section: rawSection,
    legacySlug: byId == null,
    query,
  };
}

/** Replace only the hash route, retaining filters and other hash segments. */
export function withProjectHubRoute(
  currentHash: string,
  projectId: string,
  section: ProjectRailKey,
  query = '',
): string {
  return withRouteSegment(currentHash, `${projectHubRoute(projectId, section)}${query}`);
}

export function isProjectHubRoute(hash: string): boolean {
  return routeSegmentOf(hash)?.startsWith(PROJECTS_PREFIX) ?? false;
}

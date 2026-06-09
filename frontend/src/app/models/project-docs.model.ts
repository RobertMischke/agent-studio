export interface SecurityMeta {
  lastReviewDate: string | null;
  rating: string | null;
  summary: string | null;
}

export interface SecurityFileEntry {
  name: string;
  relPath: string;
  updatedAt: string;
  size: number;
}

export interface SecurityOverview {
  projectName: string;
  baseDir: string;
  exists: boolean;
  meta: SecurityMeta;
  files: SecurityFileEntry[];
}

export interface SecurityFileContent {
  relPath: string;
  content: string;
}

export interface WikiFileEntry {
  name: string;
  relPath: string;
  title: string;
  updatedAt: string;
  size: number;
}

export interface WikiOverview {
  projectName: string;
  baseDir: string;
  exists: boolean;
  files: WikiFileEntry[];
}

export interface WikiFileContent {
  relPath: string;
  content: string;
}

/** Per-document provenance parsed from YAML frontmatter (mirrors backend WikiDocMetadata). */
export interface WikiDocMetadata {
  model: string | null;
  updatedAt: string | null;
  reason: string | null;
  taskKey: string | null;
  status: string | null;
  runCount: string | null;
  hasFrontmatter: boolean;
}

/** One git commit that touched a wiki doc (mirrors backend GitCommitInfo). */
export interface WikiCommitInfo {
  sha: string;
  shortSha: string;
  authorDateUtc: string;
  author: string;
  subject: string;
  filesChanged: number;
  added: number;
  removed: number;
}

/** Provenance + git history for a single wiki document. */
export interface WikiFileHistory {
  relPath: string;
  model: string | null;
  metadata: WikiDocMetadata;
  commits: WikiCommitInfo[];
}

/**
 * One node in the user-defined wiki organisation manifest. A `group` is a
 * user-created theme (no relPath); a `doc` pins an existing wiki file into the
 * hierarchy (relPath set, title optionally overriding the file's own title).
 */
export interface WikiOrgNode {
  id: string;
  type: 'group' | 'doc';
  title: string | null;
  relPath: string | null;
  parentId: string | null;
  order: number;
}

/** The virtual organisation layer persisted as docs/.wiki-organization.json. */
export interface WikiOrganization {
  version: number;
  nodes: WikiOrgNode[];
}

export interface ArchitectureDecisionSummary {
  id: string;
  title: string;
  date: string | null;
  status: string;
  body: string;
}

export interface ArchitectureOverview {
  projectName: string;
  sourceFile: string;
  exists: boolean;
  preamble: string;
  decisions: ArchitectureDecisionSummary[];
}

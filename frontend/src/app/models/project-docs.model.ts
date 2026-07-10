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

export interface WikiFileSaveResult {
  relPath: string;
  saved: boolean;
  changed: boolean;
  sha: string | null;
  branch: string | null;
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
  relatedTasks?: RelatedTaskReference[];
}

export interface RelatedTaskReference {
  key: string;
  title: string;
  linkedAt: string;
  source: 'auto' | 'manual';
  exists: boolean | null;
}

/** Kind of a wiki tree node: a folder, or a document by source type. */
export type WikiNodeType = 'folder' | 'md' | 'html' | 'json';

/** Compact per-document metadata shown in the tree (mirrors backend WikiTreeMetadata). */
export interface WikiTreeMetadata {
  documentMode: string | null;
  temporalState: string | null;
  implementationState: string | null;
  driftGrade: string | null;
  hasDrift: boolean | null;
  driftScore: number | null;
  quality: string | null;
  duplicateSuspected: boolean | null;
  duplicateGroupSize: number | null;
  reportPath: string | null;
  summary: string | null;
  companionPath: string | null;
  sourceChangedSinceReview: boolean | null;
  findingsCount: number | null;
}

/**
 * One node in the physical wiki tree (mirrors backend WikiTreeNode). A `folder`
 * carries `children`; a document node (`md` / `html` / `json`) is a leaf whose
 * `relPath` is the docs-root-relative path. `title` is the display label (first
 * H1 or JSON title for docs, order-prefix-stripped name otherwise). `metadata`
 * is present only when an adjacent `<source-file>.meta.json` companion
 * describes the source document.
 */
export interface WikiTreeNode {
  name: string;
  title: string;
  relPath: string | null;
  type: WikiNodeType;
  children: WikiTreeNode[];
  metadata?: WikiTreeMetadata | null;
  /**
   * True for a fixed Engineering Workstream frame node (a frame folder or a
   * landing shell). The tree marks such nodes with a lock affordance and the
   * context menu suppresses rename/delete/move so the frame's shape stays
   * stable. Mirrors backend `WikiTreeNode.Immutable`.
   */
  immutable?: boolean;
}

/** The physical docs/ folder tree backing the wiki navigation. */
export interface WikiTree {
  projectName: string;
  baseDir: string;
  exists: boolean;
  root: WikiTreeNode[];
}

/** One recently-edited wiki page (page / git author / when), newest first. */
export interface WikiRecentEdit {
  relPath: string;
  title: string;
  author: string;
  authorDateUtc: string;
  sha: string;
  shortSha: string;
  subject: string;
}

/** Recent-edits payload backing the wiki dashboard landing surface. */
export interface WikiRecentEdits {
  projectName: string;
  baseDir: string;
  exists: boolean;
  edits: WikiRecentEdit[];
}

/** Content of a wiki doc at a past commit (the "view old revision" payload). */
export interface WikiRevisionContent {
  relPath: string;
  sha: string;
  content: string;
}

// ---- Wiki Pulse (PULSE-1: the generated wiki landing view) ----

/** One change-feed row: a recently-edited page + frame-area badge + task key. */
export interface WikiPulseFeedItem {
  relPath: string;
  title: string;
  author: string;
  authorDateUtc: string;
  sha: string;
  shortSha: string;
  subject: string;
  frameAreaSlug: string | null;
  frameAreaTitle: string | null;
  taskKey: string | null;
}

/** Change-feed section (recently-edited pages, newest first). */
export interface WikiPulseFeed {
  available: boolean;
  reason: string | null;
  items: WikiPulseFeedItem[];
}

/** One unfiled page plus the reason it landed in the inbox. */
export interface WikiPulseInboxItem {
  relPath: string;
  title: string;
  type: WikiNodeType;
  reason: string;
}

/** Inbox section (loose / unfiled pages; an empty list is healthy). */
export interface WikiPulseInbox {
  available: boolean;
  reason: string | null;
  count: number;
  items: WikiPulseInboxItem[];
}

/** One frame area's drift grade (worst page band + code-commit counts). */
export interface WikiPulseDriftArea {
  slug: string;
  title: string;
  grade: string; // Fresh | Aging | Stale | Empty
  pageCount: number;
  gradedPageCount: number;
  worstCommitCount: number;
  freshCount: number;
  agingCount: number;
  staleCount: number;
}

/** Roll-up of how many graded pages fall in each drift band. */
export interface WikiPulseDriftCounts {
  fresh: number;
  aging: number;
  stale: number;
  graded: number;
}

/** Drift-grading section (the per-area grade bar + roll-up counts). */
export interface WikiPulseDrift {
  available: boolean;
  reason: string | null;
  overallGrade: string; // Fresh | Aging | Stale | Empty
  areas: WikiPulseDriftArea[];
  counts: WikiPulseDriftCounts;
}

/**
 * The generated wiki Pulse landing view (PULSE-1): a read-only composition of
 * the change feed, the sort-needed inbox, and the deterministic drift grade
 * bar. Not a wiki page - it is generated, never editable. Each section carries
 * its own `available` + `reason` so a missing source degrades to an empty state.
 */
export interface WikiPulse {
  projectName: string;
  baseDir: string;
  exists: boolean;
  generatedAtUtc: string;
  feed: WikiPulseFeed;
  inbox: WikiPulseInbox;
  drift: WikiPulseDrift;
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

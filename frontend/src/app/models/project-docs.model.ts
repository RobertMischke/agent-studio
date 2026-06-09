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

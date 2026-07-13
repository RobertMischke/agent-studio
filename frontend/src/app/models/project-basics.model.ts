import type { CliType, RegistryProjectSummary, RegistryWorkspaceListItem } from './task.model';

export const PROJECT_COLOR_SWATCHES = [
  '#569cd6',
  '#4ec9b0',
  '#c586c0',
  '#d97757',
  '#f59e0b',
  '#8b5cf6',
] as const;

export interface ProjectBasicsValue {
  workspaceId: string;
  displayName: string;
  shortCode: string;
  color: string;
  repositoryPath: string;
  rootPath: string;
  repositoryUrl: string;
  agentOverrideEnabled: boolean;
  cliDefault: CliType;
  modelDefault: string;
}

export type ProjectBasicsField =
  | 'workspaceId'
  | 'displayName'
  | 'shortCode'
  | 'repositoryPath'
  | 'rootPath'
  | 'repositoryUrl';

export type ProjectBasicsValidationErrors = Partial<Record<ProjectBasicsField, string>>;

export interface ProjectBasicsValidationContext {
  workspaces?: readonly RegistryWorkspaceListItem[];
  projects?: readonly RegistryProjectSummary[];
  currentProjectId?: string | null;
}

export function deriveProjectShortCode(value: string): string {
  if (!value.trim()) return '';
  const words = value.trim().split(/[^A-Za-z0-9]+/)
    .map((word) => word.replace(/^[^A-Za-z]+/, ''))
    .filter(Boolean);
  if (words.length === 0) return 'PROJ';
  const seed = words.length === 1
    ? words[0].slice(0, 3)
    : words.slice(0, 3).map((word) => word[0]).join('');
  const normalized = normalizeProjectShortCode(seed);
  return normalized.length >= 2 ? normalized : `${normalized}P`;
}

export function normalizeProjectShortCode(value: string): string {
  return value.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 6);
}

export function validateProjectBasics(
  value: ProjectBasicsValue,
  context: ProjectBasicsValidationContext = {},
): ProjectBasicsValidationErrors {
  const errors: ProjectBasicsValidationErrors = {};
  const workspaceId = value.workspaceId.trim();
  const displayName = value.displayName.trim();
  const shortCode = value.shortCode.trim().toUpperCase();

  if (!workspaceId) {
    errors.workspaceId = 'Choose a workspace.';
  } else if (context.workspaces?.length && !context.workspaces.some((workspace) => workspace.id === workspaceId)) {
    errors.workspaceId = 'The selected workspace is no longer available.';
  }

  if (!displayName) {
    errors.displayName = 'Enter a project name.';
  } else if (displayName.length > 64) {
    errors.displayName = 'Use 64 characters or fewer.';
  } else if (context.projects?.some((project) =>
    project.id !== context.currentProjectId
    && project.displayName.trim().toLowerCase() === displayName.toLowerCase(),
  )) {
    errors.displayName = `A project named ${displayName} already exists.`;
  }

  if (!/^[A-Z][A-Z0-9]{1,5}$/.test(shortCode)) {
    errors.shortCode = 'Use 2-6 characters, starting with A-Z; the rest may also contain 0-9.';
  } else {
    const duplicate = context.projects?.some((project) =>
      project.id !== context.currentProjectId
      && project.shortCode.toUpperCase() === shortCode,
    );
    if (duplicate) errors.shortCode = `Short code ${shortCode} is already in use.`;
  }

  const repositoryPath = value.repositoryPath.trim();
  if (repositoryPath && !isAbsoluteLocalPath(repositoryPath)) {
    errors.repositoryPath = 'Enter an absolute local Windows or POSIX path. Network paths are not supported.';
  }

  const rootPath = value.rootPath.trim();
  if (rootPath && !isAbsoluteLocalPath(rootPath)) {
    errors.rootPath = 'Enter an absolute local Windows or POSIX path. Network paths are not supported.';
  }

  const repositoryUrl = value.repositoryUrl.trim();
  if (repositoryUrl && !isHttpUrl(repositoryUrl)) {
    errors.repositoryUrl = 'Enter an absolute http or https URL.';
  }

  return errors;
}

export function projectBasicsAreValid(errors: ProjectBasicsValidationErrors): boolean {
  return Object.keys(errors).length === 0;
}

export function projectRepositoryUrl(project: RegistryProjectSummary): string {
  if (project.repositoryUrl) return project.repositoryUrl;
  return project.urls.find((url) => url.id === 'repo')?.url ?? '';
}

export function effectiveProjectRootPath(rootPath: string, repositoryPath: string): string {
  return rootPath.trim() || repositoryPath.trim();
}

function isAbsoluteLocalPath(value: string): boolean {
  return /^[A-Za-z]:[\\/]+[^\\/]/.test(value) || /^\/(?!\/)[^/]/.test(value);
}

function isHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return (url.protocol === 'http:' || url.protocol === 'https:') && url.hostname.length > 0;
  } catch {
    return false;
  }
}

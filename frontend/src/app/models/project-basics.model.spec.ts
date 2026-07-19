import { describe, expect, it } from 'vitest';
import {
  deriveProjectShortCode,
  effectiveProjectRootPath,
  projectBasicsAreValid,
  validateProjectBasics,
  type ProjectBasicsValue,
} from './project-basics.model';
import type { RegistryProjectSummary, RegistryWorkspaceListItem } from './task.model';

const base: ProjectBasicsValue = {
  workspaceId: 'ws-default',
  displayName: 'Quality Studio',
  shortCode: 'QS',
  color: '#569cd6',
  repositoryPath: 'C:/Projects/quality-studio',
  rootPath: 'C:/Projects/quality-studio/frontend',
  repositoryUrl: 'https://github.com/example/quality-studio',
  agentOverrideEnabled: true,
  cliDefault: 'claude',
  modelDefault: 'claude-sonnet',
};

const projects = [{ id: 'PROJ-001', displayName: 'Existing project', shortCode: 'USED' }] as RegistryProjectSummary[];
const workspaces = [{ id: 'ws-default' }] as RegistryWorkspaceListItem[];

describe('project basics validation', () => {
  it('derives a compact uppercase code from the display name', () => {
    expect(deriveProjectShortCode('Quality Studio')).toBe('QS');
    expect(deriveProjectShortCode('atlas')).toBe('ATL');
    expect(deriveProjectShortCode('A')).toBe('AP');
    expect(deriveProjectShortCode('123 Project')).toBe('PRO');
    expect(deriveProjectShortCode('123')).toBe('PROJ');
  });

  it('falls the working directory back to the checkout path when saving settings', () => {
    expect(effectiveProjectRootPath('', 'C:/Projects/quality-studio'))
      .toBe('C:/Projects/quality-studio');
    expect(effectiveProjectRootPath('C:/Projects/quality-studio/frontend', 'C:/Projects/quality-studio'))
      .toBe('C:/Projects/quality-studio/frontend');
  });

  it('accepts complete Windows values and optional blank locations', () => {
    expect(projectBasicsAreValid(validateProjectBasics(base, { projects, workspaces }))).toBe(true);
    expect(projectBasicsAreValid(validateProjectBasics({
      ...base,
      repositoryPath: '',
      rootPath: '',
      repositoryUrl: '',
    }, { projects, workspaces }))).toBe(true);
  });

  it('accepts POSIX paths and rejects network paths', () => {
    const errors = validateProjectBasics({
      ...base,
      repositoryPath: '\\\\build-host\\repos\\quality-studio',
      rootPath: '/srv/quality-studio',
    }, { projects, workspaces });
    expect(errors.repositoryPath).toContain('Network paths are not supported');
    expect(errors.rootPath).toBeUndefined();
  });

  it('rejects bare filesystem roots', () => {
    const errors = validateProjectBasics({
      ...base,
      repositoryPath: 'C:/',
      rootPath: '/',
    });
    expect(errors.repositoryPath).toBeDefined();
    expect(errors.rootPath).toBeDefined();
  });

  it('rejects malformed codes, duplicate codes, relative paths, and non-http URLs', () => {
    expect(validateProjectBasics({
      ...base,
      shortCode: '1',
      repositoryPath: '../quality-studio',
      rootPath: 'frontend',
      repositoryUrl: 'ssh://git@example.com/repo.git',
    }, { projects, workspaces })).toMatchObject({
      shortCode: expect.any(String),
      repositoryPath: expect.any(String),
      rootPath: expect.any(String),
      repositoryUrl: expect.any(String),
    });

    expect(validateProjectBasics({ ...base, shortCode: 'used' }, { projects, workspaces }).shortCode)
      .toContain('already in use');
  });

  it('enforces the backend display-name limit', () => {
    expect(validateProjectBasics({ ...base, displayName: 'x'.repeat(65) }).displayName)
      .toBe('Use 64 characters or fewer.');
  });

  it('allows the current project to keep its existing short code', () => {
    const errors = validateProjectBasics({ ...base, shortCode: 'USED' }, {
      projects,
      workspaces,
      currentProjectId: 'PROJ-001',
    });
    expect(errors.shortCode).toBeUndefined();
  });

  it('rejects a duplicate display name case-insensitively', () => {
    const peers = [{ id: 'PROJ-002', displayName: 'quality studio', shortCode: 'OTHER' }] as RegistryProjectSummary[];
    expect(validateProjectBasics(base, { projects: peers }).displayName).toContain('already exists');
  });
});

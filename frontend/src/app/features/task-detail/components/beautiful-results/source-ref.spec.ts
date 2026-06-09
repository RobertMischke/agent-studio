import { describe, expect, it } from 'vitest';
import { detectSourceRef } from './source-ref';

describe('detectSourceRef', () => {
  it('detects a plain repo source path', () => {
    expect(detectSourceRef('backend/Services/Runner/SolutionQualityGate.cs')).toEqual({
      path: 'backend/Services/Runner/SolutionQualityGate.cs',
      line: null,
    });
  });

  it('detects a bare filename with an extension', () => {
    expect(detectSourceRef('task.model.ts')).toEqual({ path: 'task.model.ts', line: null });
  });

  it('parses a trailing :line suffix', () => {
    expect(detectSourceRef('src/app/foo.ts:42')).toEqual({ path: 'src/app/foo.ts', line: 42 });
  });

  it('parses a :line:col suffix and keeps only the line', () => {
    expect(detectSourceRef('src/app/foo.ts:42:7')).toEqual({ path: 'src/app/foo.ts', line: 42 });
  });

  it('parses a GitHub-style #Lline suffix', () => {
    expect(detectSourceRef('src/app/foo.ts#L128')).toEqual({ path: 'src/app/foo.ts', line: 128 });
  });

  it('strips a leading ./', () => {
    expect(detectSourceRef('./src/app/foo.ts')).toEqual({ path: 'src/app/foo.ts', line: null });
  });

  it('normalizes backslashes to forward slashes', () => {
    expect(detectSourceRef('backend\\Services\\Foo.cs')).toEqual({
      path: 'backend/Services/Foo.cs',
      line: null,
    });
  });

  it('ignores a zero or negative line number', () => {
    expect(detectSourceRef('src/app/foo.ts:0')).toEqual({ path: 'src/app/foo.ts', line: null });
  });

  it('rejects empty / whitespace-only input', () => {
    expect(detectSourceRef('')).toBeNull();
    expect(detectSourceRef('   ')).toBeNull();
    expect(detectSourceRef(null)).toBeNull();
    expect(detectSourceRef(undefined)).toBeNull();
  });

  it('rejects strings containing whitespace (shell snippets, prose)', () => {
    expect(detectSourceRef('npm run build')).toBeNull();
    expect(detectSourceRef('git status')).toBeNull();
    expect(detectSourceRef('see foo.ts here')).toBeNull();
  });

  it('rejects absolute paths', () => {
    expect(detectSourceRef('/etc/passwd')).toBeNull();
    expect(detectSourceRef('/src/app/foo.ts')).toBeNull();
  });

  it('rejects parent-directory traversal', () => {
    expect(detectSourceRef('../secrets/foo.ts')).toBeNull();
    expect(detectSourceRef('src/../../foo.ts')).toBeNull();
  });

  it('rejects prose-y dotted tokens without a source extension', () => {
    expect(detectSourceRef('e.g.')).toBeNull();
    expect(detectSourceRef('i.e.something')).toBeNull();
    expect(detectSourceRef('foo.bar')).toBeNull();
  });

  it('rejects a path with no filename extension', () => {
    expect(detectSourceRef('src/app/foo')).toBeNull();
    expect(detectSourceRef('Makefile')).toBeNull();
  });

  it('rejects a dotfile-only segment (extension equals the whole name)', () => {
    expect(detectSourceRef('src/.gitignore')).toBeNull();
  });
});

import { describe, expect, it } from 'vitest';
import { cleanStepResultMarkdown } from './pipeline-step-result.util';

describe('cleanStepResultMarkdown', () => {
  it('returns empty string for nullish / empty input', () => {
    expect(cleanStepResultMarkdown(null)).toBe('');
    expect(cleanStepResultMarkdown(undefined)).toBe('');
    expect(cleanStepResultMarkdown('')).toBe('');
  });

  it('passes a clean status.md through unchanged (no frontmatter / fences)', () => {
    const status = [
      '# Status',
      '',
      '- Result: Success',
      '',
      '## What Was Done',
      '',
      '- Did the thing.',
    ].join('\n');
    expect(cleanStepResultMarkdown(status)).toBe(status);
  });

  it('strips a leading YAML frontmatter block', () => {
    const raw = [
      '---',
      'aspect: code-quality',
      'status: pass',
      '---',
      '',
      '# Aspect: code-quality',
      '',
      '**Status:** pass',
    ].join('\n');
    const out = cleanStepResultMarkdown(raw);
    expect(out.startsWith('# Aspect: code-quality')).toBe(true);
    expect(out).not.toContain('aspect: code-quality');
    expect(out).not.toContain('---');
  });

  it('unwraps the Model reply fence and drops trailing sentinels', () => {
    const raw = [
      '---',
      'aspect: code-quality',
      'status: pass',
      '---',
      '',
      '# Aspect: code-quality',
      '',
      '## Model reply',
      '',
      '```',
      '## Code Quality Review',
      '',
      'No production code changes.',
      '```',
      '[[ASPECT_VERDICT: status=pass; summary=ok]]',
      '```',
      '',
      '[[TASK_DONE]]',
      '```',
    ].join('\n');
    const out = cleanStepResultMarkdown(raw);
    // The model reply renders as a real heading, not inside a code fence.
    expect(out).toContain('## Code Quality Review');
    expect(out).toContain('No production code changes.');
    // No machine sentinels survive.
    expect(out).not.toContain('ASPECT_VERDICT');
    expect(out).not.toContain('TASK_DONE');
    // No dangling empty code fences remain.
    expect(out).not.toMatch(/```\s*```/);
  });

  it('unwraps the Reviewer reply fence used by code-review reports', () => {
    const raw = [
      '---',
      'type: code-review-step',
      'verdict: concerns',
      '---',
      '',
      '# Code Review Step',
      '',
      '**Verdict:** concerns',
      '',
      '## Reviewer reply',
      '',
      '```',
      '### Findings',
      '',
      '- Fix `token` handling.',
      '',
      '[[ASPECT_VERDICT: status=concerns; summary=token handling]]',
      '```',
    ].join('\n');
    const out = cleanStepResultMarkdown(raw);
    expect(out).toContain('### Findings');
    expect(out).toContain('- Fix `token` handling.');
    expect(out).not.toContain('ASPECT_VERDICT');
    expect(out).not.toMatch(/```\s*### Findings/);
  });

  it('keeps a fenced code block that carries a language tag', () => {
    const raw = ['Some prose.', '', '```ts', 'const a = 1;', '```'].join('\n');
    const out = cleanStepResultMarkdown(raw);
    expect(out).toContain('```ts');
    expect(out).toContain('const a = 1;');
  });
});

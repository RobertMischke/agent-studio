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

  it('replaces a raw codex JSONL event dump with the agent_message text', () => {
    const raw = [
      '## Reviewer reply',
      '',
      '{"type":"thread.started","thread_id":"019f8c22-77c2"}',
      '{"type":"turn.started"} {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Quality-Grade B. The change works.\\n\\n[[CODE_REVIEW_GRADE: grade=B; summary=Done.]]\\n\\n[[TASK_DONE]]"}}',
      '{"type":"turn.completed","usage":{"input_tokens":38104,"output_tokens":8794}}',
    ].join('\n');
    const out = cleanStepResultMarkdown(raw);
    expect(out).toContain('Quality-Grade B. The change works.');
    expect(out).not.toContain('"type"');
    expect(out).not.toContain('thread.started');
    expect(out).not.toContain('input_tokens');
    expect(out).not.toContain('CODE_REVIEW_GRADE');
    expect(out).not.toContain('[[TASK_DONE]]');
  });

  it('leaves prose containing braces untouched when it is not an event dump', () => {
    const raw = [
      '## Reviewer reply',
      '',
      'Configure it via {"type": "manual"} in the settings file.',
      'Interfaces like Foo {} stay as written.',
    ].join('\n');
    const out = cleanStepResultMarkdown(raw);
    expect(out).toContain('Configure it via {"type": "manual"} in the settings file.');
    expect(out).toContain('Interfaces like Foo {} stay as written.');
  });
});

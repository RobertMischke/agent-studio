import { describe, expect, it } from 'vitest';
import type { TaskDetail } from '../../../../models/task.model';
import type { ProtocolVerdict } from './protocol-verdict';
import { buildResultDocument, codeReviewGradeFromTags, parseCaseHint } from './result-document';

function verdict(overrides: Partial<ProtocolVerdict> = {}): ProtocolVerdict {
  return {
    kind: 'ok',
    emoji: '🟢',
    label: 'Success',
    detail: 'Last run completed successfully.',
    duration: null,
    superseded: null,
    ...overrides,
  };
}

interface DetailOpts {
  statusMarkdown?: string | null;
  title?: string;
  taskType?: string | null;
  mode?: string | null;
  tags?: string[];
  totalTokens?: number;
  commits?: number;
  codeActivityDetected?: boolean;
}

function detail(opts: DetailOpts = {}): TaskDetail {
  return {
    info: {
      id: 'job-1',
      watchPath: '/ws',
      title: opts.title ?? 'Fix the broken thing',
      taskType: opts.taskType ?? 'chore',
      mode: opts.mode ?? 'coding',
      tags: opts.tags ?? [],
      tokenSummary: opts.totalTokens != null ? ({ totalTokens: opts.totalTokens } as never) : null,
      commits: opts.commits != null ? new Array(opts.commits).fill({}) : [],
      codeActivityDetected: opts.codeActivityDetected,
    },
    statusMarkdown: opts.statusMarkdown ?? null,
  } as unknown as TaskDetail;
}

const STATUS_WITH_OVERVIEW = `# Status

- Result: Success
- Duration: 4 min

## Overview
- Problem: The verdict banner was unreadable on the light theme.
- Solution: Re-toned it to a neutral surface with a semantic accent stripe.

## What Was Done
- Edited \`banner.scss\` to use tokens.
- Added a regression spec.

## Open Items
- Wire the same tokens into the dark theme sweep.
- None.

## Images
- ![](results/banner--real.png)
`;

const STATUS_LEGACY = `# Status

- Result: Success
- Duration: 2 min

## What Was Done
- Renamed the helper and deduplicated the call sites.

## Open Items
- None.
`;

describe('codeReviewGradeFromTags', () => {
  it('reads the A-D grade from the code-review grade tag', () => {
    expect(codeReviewGradeFromTags(['x', 'code-review:grade-b', 'y'])).toBe('B');
    expect(codeReviewGradeFromTags(['CODE-REVIEW:GRADE-A'])).toBe('A');
  });
  it('returns null without a grade tag', () => {
    expect(codeReviewGradeFromTags([])).toBeNull();
    expect(codeReviewGradeFromTags(['area:frontend'])).toBeNull();
    expect(codeReviewGradeFromTags(null)).toBeNull();
  });
});

describe('parseCaseHint', () => {
  it('pulls a `- Case:` line out of the markdown', () => {
    expect(parseCaseHint('# Status\n\n- Case: ui-cleanup\n\n## Overview')).toBe('ui-cleanup');
  });
  it('returns null when absent', () => {
    expect(parseCaseHint('# Status\n\n## Overview')).toBeNull();
    expect(parseCaseHint(null)).toBeNull();
  });
});

describe('buildResultDocument', () => {
  it('parses an explicit ## Overview into problem/solution (not synthesized)', () => {
    const doc = buildResultDocument(detail({ statusMarkdown: STATUS_WITH_OVERVIEW }), verdict({ duration: '4 min' }));
    expect(doc.overview.synthesized).toBe(false);
    expect(doc.overview.problem).toContain('unreadable on the light theme');
    expect(doc.overview.solution).toContain('neutral surface');
  });

  it('synthesizes an overview from title + first What Was Done bullet on legacy status.md', () => {
    const doc = buildResultDocument(detail({ statusMarkdown: STATUS_LEGACY, title: 'Dedup helper' }), verdict());
    expect(doc.overview.synthesized).toBe(true);
    expect(doc.overview.problem).toBe('Dedup helper');
    expect(doc.overview.solution).toContain('Renamed the helper');
  });

  it('strips the # Status and ## Overview blocks from the detail markdown', () => {
    const doc = buildResultDocument(detail({ statusMarkdown: STATUS_WITH_OVERVIEW }), verdict({ duration: '4 min' }));
    expect(doc.detailMarkdown).not.toContain('# Status');
    expect(doc.detailMarkdown).not.toContain('## Overview');
    expect(doc.detailMarkdown).not.toContain('unreadable on the light theme');
    // Everything below the overview survives.
    expect(doc.detailMarkdown).toContain('## What Was Done');
    expect(doc.detailMarkdown).toContain('## Images');
    expect(doc.hasDetail).toBe(true);
  });

  it('counts open items (excluding "None.") and images', () => {
    const doc = buildResultDocument(detail({ statusMarkdown: STATUS_WITH_OVERVIEW }), verdict({ duration: '4 min' }));
    expect(doc.openItemsCount).toBe(1);
    expect(doc.imagesCount).toBe(1);
  });

  it('always emits a verdict metric and reflects tone', () => {
    const doc = buildResultDocument(detail(), verdict({ kind: 'problem', label: 'Blocked', emoji: '🔴' }));
    const m = doc.metrics.find((x) => x.id === 'verdict');
    expect(m?.value).toBe('Blocked');
    expect(m?.tone).toBe('problem');
  });

  it('adds a grade chip from the code-review tag', () => {
    const doc = buildResultDocument(detail({ tags: ['code-review:grade-a'] }), verdict());
    const grade = doc.metrics.find((x) => x.id === 'grade');
    expect(grade?.value).toBe('Grade A');
    expect(grade?.tone).toBe('ok');
  });

  it('adds duration + tokens + commits chips when the data is present', () => {
    const doc = buildResultDocument(
      detail({ totalTokens: 1_500_000, commits: 2 }),
      verdict({ duration: '4 min' }),
    );
    expect(doc.metrics.find((x) => x.id === 'duration')?.value).toBe('4 min');
    expect(doc.metrics.find((x) => x.id === 'tokens')).toBeTruthy();
    expect(doc.metrics.find((x) => x.id === 'commits')?.value).toBe('2 commits');
  });

  it('renders a "no code change" commits chip when the scanner saw no activity', () => {
    const doc = buildResultDocument(detail({ commits: 0, codeActivityDetected: false }), verdict());
    expect(doc.metrics.find((x) => x.id === 'commits')?.value).toBe('no code change');
  });

  it('classifies the case from metadata (bug task -> bugfix)', () => {
    const doc = buildResultDocument(detail({ taskType: 'bug', statusMarkdown: STATUS_LEGACY }), verdict());
    expect(doc.case.case).toBe('bugfix');
  });

  it('leads with the blocked case when the verdict is a problem', () => {
    const doc = buildResultDocument(detail({ taskType: 'feature' }), verdict({ kind: 'problem', label: 'Blocked' }));
    expect(doc.case.case).toBe('blocked');
  });
});

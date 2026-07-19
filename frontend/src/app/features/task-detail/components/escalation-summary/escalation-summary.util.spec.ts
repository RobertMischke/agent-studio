import { describe, expect, it } from 'vitest';
import type { TaskInfo, ReviewEvidenceEntry } from '../../../../models/task.model';
import type { CodeReviewListEntry } from '../../../../services/task.service';
import type { SteeringInfo } from '../../../../components/steering-detail';
import {
  buildDelivery,
  buildEscalationSummaryView,
  deriveEscalationClass,
  deriveReissues,
  deriveRecommendation,
  gateItemsFromEvidence,
  gateItemsFromFindings,
  gradeTone,
  parseFollowUpGateItems,
  parseStatusStubEscalation,
  pickReviewHead,
  resolveGateItems,
} from './escalation-summary.util';

function evidence(over: Partial<ReviewEvidenceEntry> = {}): ReviewEvidenceEntry {
  return {
    id: over.id ?? 'e1',
    source: over.source ?? 'code-review',
    severity: over.severity ?? 'warn',
    title: over.title ?? 'Finding',
    body: over.body ?? null,
    createdAt: over.createdAt ?? '2026-07-09T10:00:00Z',
    runIndex: over.runIndex ?? null,
    artifacts: over.artifacts ?? [],
    fileRefs: over.fileRefs ?? [],
    acknowledged: over.acknowledged ?? false,
    followupJobId: over.followupJobId ?? null,
  };
}

function review(over: Partial<CodeReviewListEntry> = {}): CodeReviewListEntry {
  return {
    fileName: over.fileName ?? 'code-review-grade-2026-07-09T19-22-02Z.md',
    verdict: over.verdict ?? 'pass',
    grade: over.grade,
    summary: over.summary ?? 'Solid first slice.',
    model: over.model ?? 'claude-opus-4-8',
    cliType: over.cliType ?? 'claude',
    runAt: over.runAt ?? '2026-07-09T19:22:02Z',
  };
}

function info(over: Partial<TaskInfo> = {}): TaskInfo {
  return { orchestratorVerdict: 'escalate', ...(over as object) } as TaskInfo;
}

describe('parseFollowUpGateItems', () => {
  it('lifts task-list rows and ignores free-form preamble', () => {
    const md = [
      '# Orchestrator follow-up',
      '',
      'STEER THE DIFF, DO NOT RESTART: close out only the open items.',
      '',
      '- [ ] Frontend Playwright verification skipped (worktree limitation).',
      '- [x] Live Haiku probe not run.',
      '* [ ] JSON aspect artefacts left for follow-up.',
    ].join('\n');
    const items = parseFollowUpGateItems(md);
    expect(items).toHaveLength(3);
    expect(items[0]).toEqual({
      text: 'Frontend Playwright verification skipped (worktree limitation).',
      checked: false,
    });
    expect(items[1].checked).toBe(true);
    expect(items[2].checked).toBe(false);
  });

  it('returns [] for null or checklist-free markdown', () => {
    expect(parseFollowUpGateItems(null)).toEqual([]);
    expect(parseFollowUpGateItems('Just prose, no boxes.')).toEqual([]);
  });
});

describe('gateItemsFromFindings', () => {
  it('renders aspect findings as unchecked toned items', () => {
    const items = gateItemsFromFindings([
      { aspect: 'code-quality', verdict: 'block', reason: 'helper duplicated' },
      { aspect: 'requirement-fit', verdict: 'concerns', reason: '' },
    ]);
    expect(items[0]).toMatchObject({
      text: 'code-quality: helper duplicated',
      checked: false,
      verdict: 'block',
      tone: 'danger',
    });
    // No reason -> label only, warn tone for concerns.
    expect(items[1]).toMatchObject({ text: 'requirement-fit', tone: 'warn' });
  });
});

describe('gateItemsFromEvidence', () => {
  it('drops acknowledged findings and orders high severity first', () => {
    const items = gateItemsFromEvidence([
      evidence({ id: 'a', severity: 'info', title: 'info one' }),
      evidence({ id: 'b', severity: 'high', title: 'high one' }),
      evidence({ id: 'c', severity: 'warn', title: 'ack', acknowledged: true }),
    ]);
    expect(items.map((i) => i.text)).toEqual(['high one', 'info one']);
    expect(items[0].tone).toBe('danger');
  });
});

describe('resolveGateItems priority', () => {
  const steering: SteeringInfo = {
    verdict: 'escalate',
    verdictLabel: 'Escalate',
    tone: 'danger',
    reason: 'completion gate found unfinished work',
    openItems: [{ aspect: 'tests', verdict: 'block', reason: 'missing regression' }],
    prompt: null,
    context: [{ key: 'Cause', value: 'completion-gate' }],
    commits: [],
  };

  it('prefers the follow-up checklist over findings and evidence', () => {
    const { items, source } = resolveGateItems({
      followUpMarkdown: '- [ ] do the thing',
      steering,
      reviewEvidence: [evidence()],
    });
    expect(source).toBe('follow-up');
    expect(items).toHaveLength(1);
  });

  it('falls back to gate findings when no follow-up file', () => {
    const { source } = resolveGateItems({
      followUpMarkdown: null,
      steering,
      reviewEvidence: [evidence()],
    });
    expect(source).toBe('gate-evidence');
  });

  it('falls back to review evidence when neither follow-up nor findings exist', () => {
    const { source, items } = resolveGateItems({
      followUpMarkdown: null,
      steering: null,
      reviewEvidence: [evidence({ title: 'lonely finding' })],
    });
    expect(source).toBe('review-evidence');
    expect(items[0].text).toBe('lonely finding');
  });

  it('reports none when there is nothing to show', () => {
    expect(
      resolveGateItems({ followUpMarkdown: null, steering: null, reviewEvidence: [] }).source,
    ).toBe('none');
  });
});

describe('gradeTone', () => {
  it('maps A/B to ok, C to warn, D to danger, unknown to neutral', () => {
    expect(gradeTone('A')).toBe('ok');
    expect(gradeTone('b')).toBe('ok');
    expect(gradeTone('C')).toBe('warn');
    expect(gradeTone('D')).toBe('danger');
    expect(gradeTone(null)).toBe('neutral');
  });
});

describe('pickReviewHead', () => {
  it('prefers the newest graded entry over an ungraded verdict', () => {
    const head = pickReviewHead([
      review({ grade: undefined, verdict: 'concerns', runAt: '2026-07-09T20:00:00Z' }),
      review({ grade: 'B', verdict: 'pass', runAt: '2026-07-09T19:00:00Z' }),
    ]);
    expect(head?.grade).toBe('B');
    expect(head?.gradeTone).toBe('ok');
    expect(head?.verdictTone).toBe('pass');
  });

  it('falls back to the newest entry when none carry a grade', () => {
    const head = pickReviewHead([
      review({ grade: undefined, verdict: 'block', runAt: '2026-07-09T21:00:00Z' }),
      review({ grade: undefined, verdict: 'pass', runAt: '2026-07-09T18:00:00Z' }),
    ]);
    expect(head?.grade).toBeNull();
    expect(head?.verdict).toBe('block');
  });

  it('returns null for an empty list', () => {
    expect(pickReviewHead([])).toBeNull();
  });
});

describe('buildDelivery', () => {
  it('counts commits and de-duplicates changed files across them', () => {
    const d = buildDelivery(
      info({
        commits: [
          { sha: 'a', shortSha: 'a', message: 'm', filesChanged: 2, files: ['x.ts', 'y.ts'], at: '' },
          { sha: 'b', shortSha: 'b', message: 'm', filesChanged: 2, files: ['y.ts', 'z.ts'], at: '' },
        ],
        mergeSignal: {
          branch: 'task/AGT-1994',
          inIntegration: true,
          inRelease: false,
          integrationBranch: 'develop',
          releaseBranch: 'main',
          integrationSha: 'abc1234',
          releaseSha: null,
        },
      } as Partial<TaskInfo>),
    );
    expect(d.commitCount).toBe(2);
    expect(d.filesChanged).toBe(3); // x, y, z distinct
    expect(d.merge?.develop.merged).toBe(true);
    expect(d.merge?.main.merged).toBe(false);
  });

  it('sums filesChanged when no file lists are present', () => {
    const d = buildDelivery(
      info({
        commits: [
          { sha: 'a', shortSha: 'a', message: 'm', filesChanged: 3, files: [], at: '' },
        ],
      } as Partial<TaskInfo>),
    );
    expect(d.filesChanged).toBe(3);
  });
});

describe('deriveRecommendation', () => {
  it('maps escalate -> needs decision (danger)', () => {
    expect(deriveRecommendation('escalate')).toEqual({
      kind: 'needs-decision',
      label: 'Needs decision',
      tone: 'danger',
    });
  });
  it('maps accept and reissue', () => {
    expect(deriveRecommendation('accept')?.kind).toBe('accept');
    expect(deriveRecommendation('reissue')?.tone).toBe('warn');
  });
  it('returns null for pending / absent', () => {
    expect(deriveRecommendation('pending')).toBeNull();
    expect(deriveRecommendation(null)).toBeNull();
  });
});

describe('buildEscalationSummaryView', () => {
  it('assembles the full view model for the AGT-1994 shape', () => {
    const view = buildEscalationSummaryView({
      info: info({
        orchestratorVerdict: 'escalate',
        commits: [
          { sha: 'b2ed3f47', shortSha: 'b2ed3f47', message: 'm', filesChanged: 4, files: ['a', 'b', 'c', 'd'], at: '' },
        ],
        mergeSignal: {
          branch: 'task/AGT-1994',
          inIntegration: true,
          inRelease: true,
          integrationBranch: 'develop',
          releaseBranch: 'main',
          integrationSha: 'b2ed3f4',
          releaseSha: '1a526e9',
        },
      } as Partial<TaskInfo>),
      reviewEvidence: [],
      codeReviews: [review({ grade: 'B', verdict: 'pass' })],
      followUpMarkdown: '- [ ] Frontend Playwright verification skipped.\n- [ ] Live Haiku probe not run.',
      steering: {
        verdict: 'escalate',
        verdictLabel: 'Escalate',
        tone: 'danger',
        reason: 'completion gate found unfinished work',
        openItems: [],
        prompt: null,
        context: [{ key: 'Cause', value: 'completion-gate' }],
        commits: [],
      },
      statusMarkdown: null,
      timeline: [
        {
          ts: '2026-07-09T18:30:00Z',
          kind: 'quality_loop_reopened',
          actor: 'quality-loop',
          summary: 'Reopened after build gate.',
          details: { cause: 'build/test gate failed', reason: 'npm test exited with 1' },
        },
        {
          ts: '2026-07-09T19:30:00Z',
          kind: 'orchestrator_escalated',
          actor: 'orchestrator',
          summary: 'Budget exhausted.',
          details: { attempt: '3', maxAttempts: '3' },
        },
      ],
    });

    expect(view.gateSource).toBe('follow-up');
    expect(view.gateItems).toHaveLength(2);
    expect(view.review?.grade).toBe('B');
    expect(view.reason).toBe('completion gate found unfinished work');
    expect(view.cause).toBe('completion-gate');
    expect(view.delivery.commitCount).toBe(1);
    expect(view.delivery.filesChanged).toBe(4);
    expect(view.delivery.merge?.main.merged).toBe(true);
    expect(view.recommendation?.kind).toBe('needs-decision');
    expect(view.reissues[0].trigger).toBe('build/test gate failed: npm test exited with 1');
    expect(view.stateSentence).toBe(
      'Delivered and merged; waiting for your decision because the reissue budget is exhausted, and 2 gate points remain open.',
    );
    // A completion-gate escalation is a logical / quality review, not a give-up.
    expect(view.escalation?.kind).toBe('needs-review');
  });
});

describe('deriveReissues', () => {
  it('numbers reopen events chronologically and explains each trigger', () => {
    const rows = deriveReissues([
      {
        ts: '2026-07-09T10:00:00Z', kind: 'quality_loop_reopened', actor: 'quality-loop',
        summary: 'reopened', details: { cause: 'build/test gate failed', reason: 'npm test exit 1' },
      },
      {
        ts: '2026-07-09T11:00:00Z', kind: 'quality_loop_reopened', actor: 'quality-loop',
        summary: 'reopened', details: { reason: 'bundle budget and apply_patch stderr' },
      },
    ]);
    expect(rows.map((row) => [row.index, row.at, row.trigger])).toEqual([
      [1, '2026-07-09T10:00:00Z', 'build/test gate failed: npm test exit 1'],
      [2, '2026-07-09T11:00:00Z', 'bundle budget and apply_patch stderr'],
    ]);
  });
});

describe('parseStatusStubEscalation', () => {
  it('lifts the category + reason from a BuildStatusStub status.md', () => {
    const md = [
      '# Status',
      '',
      '- Result: Escalated to human decision (infra-crash)',
      '',
      'This card was routed to 5e-escalated by the orchestrator runtime ...',
      '',
      '- Category: infra-crash',
      '- Reason: The agent CLI crashed hard (exitCode -1) before a verdict.',
      '- See `logs/` in this folder for the run output.',
    ].join('\n');
    expect(parseStatusStubEscalation(md)).toEqual({
      category: 'infra-crash',
      reason: 'The agent CLI crashed hard (exitCode -1) before a verdict.',
    });
  });

  it('returns null when the status carries a real agent summary (no Category line)', () => {
    expect(parseStatusStubEscalation('# Status\n\n- Result: Done. Shipped the feature.')).toBeNull();
    expect(parseStatusStubEscalation(null)).toBeNull();
  });
});

describe('deriveEscalationClass', () => {
  const noSteer = { steering: null };

  it('classifies an infra-crash status stub as a GaveUpToHuman terminal', () => {
    const cls = deriveEscalationClass({
      info: info({ orchestratorVerdict: 'escalate' }),
      statusMarkdown: '# Status\n- Category: infra-crash\n- Reason: process died at exit -1.',
      ...noSteer,
    });
    expect(cls).toEqual({
      kind: 'gave-up',
      category: 'infra-crash',
      categoryLabel: 'Infra crash',
      reason: 'process died at exit -1.',
    });
  });

  it('sniffs the aspect-infra give-up out of the steering cause when no stub exists', () => {
    const cls = deriveEscalationClass({
      info: info({ orchestratorVerdict: 'escalate' }),
      statusMarkdown: null,
      steering: {
        verdict: 'escalate',
        verdictLabel: 'Escalate',
        tone: 'danger',
        reason: 'aspect runner died',
        openItems: [],
        prompt: null,
        context: [{ key: 'Cause', value: 'aspect-verdict-infra-crash' }],
        commits: [],
      },
    });
    expect(cls?.kind).toBe('gave-up');
    expect(cls?.category).toBe('infra-crash');
    expect(cls?.reason).toBe('aspect runner died');
  });

  it('treats an escalate verdict with no give-up category as a logical NeedsReview', () => {
    const cls = deriveEscalationClass({
      info: info({ orchestratorVerdict: 'escalate' }),
      statusMarkdown: '# Status\n- Result: Done. Real summary, no category line.',
      ...noSteer,
    });
    expect(cls).toEqual({ kind: 'needs-review', category: null, categoryLabel: null, reason: null });
  });

  it('returns null when the card is not an escalation and no give-up stub exists', () => {
    expect(
      deriveEscalationClass({
        info: info({ orchestratorVerdict: 'accept' }),
        statusMarkdown: null,
        ...noSteer,
      }),
    ).toBeNull();
  });
});

import assert from 'node:assert/strict';
import test from 'node:test';

import {
  analyzeExperiment,
  renderMarkdown,
  restrictedMeanAttempts,
} from './reissue-prompt-experiment-analysis.mjs';

function record(taskId, arm, acceptedAttempt, options = {}) {
  return {
    taskId,
    arm,
    templateVersion: arm === 'control'
      ? 'runner-reissue-control-v1'
      : 'runner-reissue-treatment-v1',
    assignmentHash: taskId,
    promptFamily: options.promptFamily ?? 'model-review-finding',
    cause: options.cause ?? 'multi-aspect-block',
    firstReissueAttempt: 2,
    acceptedAttempt,
    firstGradeAAttempt: options.firstGradeAAttempt ?? acceptedAttempt,
    lastObservedAttempt: options.lastObservedAttempt ?? acceptedAttempt ?? 5,
    deterministicGateRegressed: options.deterministicGateRegressed ?? false,
    codingModel: 'gpt-fixed',
    thinkingLevel: 'medium',
    assignmentDrift: false,
    routeDrift: false,
  };
}

test('restricted mean attempts includes right-censored tasks in the risk set', () => {
  const rows = [
    { event: true, duration: 1 },
    { event: false, duration: 3 },
  ];

  assert.equal(restrictedMeanAttempts(rows, 3), 2);
});

test('analysis reports arm counts, uncertainty, censoring, and strata', () => {
  const report = analyzeExperiment([
    record('c1', 'control', 5),
    record('c2', 'control', null, { lastObservedAttempt: 5, deterministicGateRegressed: true }),
    record('t1', 'treatment', 3),
    record('t2', 'treatment', 4, { cause: 'evidence-gate', promptFamily: 'deterministic-gate' }),
  ], { bootstraps: 200, generatedAt: '2026-07-26T00:00:00.000Z' });

  assert.deepEqual(report.armCounts, { control: 2, treatment: 2 });
  assert.equal(report.primaryEndpoint.arms.control.rightCensored, 1);
  assert.equal(report.primaryEndpoint.arms.treatment.rightCensored, 0);
  assert.equal(report.primaryEndpoint.estimable, true);
  assert.ok(report.primaryEndpoint.bootstrap95Ci);
  assert.equal(report.evidenceLabels.assignmentAndAttemptEvents, 'hard');
  assert.equal(report.evidenceLabels.acceptanceEndpoint, 'model-judged');
  assert.equal(report.evidenceLabels.armComparison, 'experimental');
  assert.equal(report.promptFamilyStrata.length, 2);
  assert.equal(report.causeStrata.length, 2);
  assert.equal(report.promotionDecision.eligible, false);
  assert.equal(report.assignmentDriftTasks, 0);

  const markdown = renderMarkdown(report);
  assert.match(markdown, /Right-censored/);
  assert.match(markdown, /experimental arm comparison/);
  assert.match(markdown, /model-judged evidence/);
  assert.match(markdown, /Keep the production default unchanged/);
});

test('empty experiment remains explicit and non-promotable', () => {
  const report = analyzeExperiment([], {
    bootstraps: 10,
    generatedAt: '2026-07-26T00:00:00.000Z',
  });

  assert.equal(report.eligibleTasks, 0);
  assert.deepEqual(report.armCounts, { control: 0, treatment: 0 });
  assert.equal(report.primaryEndpoint.estimable, false);
  assert.equal(report.promotionDecision.eligible, false);
});

import { describe, expect, it } from 'vitest';
import {
  WORKBENCH_DECISION_CHANGE_MESSAGE,
  buildWorkbenchDecisionSrcdoc,
  normalizeWorkbenchDecisionAnswers,
  parseWorkbenchDecisionPoints,
  workbenchDecisionAnswersComplete,
} from './workbench-decision-markup';

const STATIC_HTML = `
  <article>
    <section data-decision-id="routing-owner" data-decision-kind="single">
      <h2>Where should routing run?</h2>
      <ul>
        <li data-option-id="task-api"><strong>Task API</strong><span>Editable recommendation.</span></li>
        <li data-option-id="studio-only"><strong>Studio only</strong><span>Catalogue default.</span></li>
      </ul>
      <p data-comment data-comment-label="Optional note">Add implementation constraints if needed.</p>
    </section>
    <section data-decision-id="policy-ready" data-decision-kind="confirm">
      <h2>Confirm the policy direction</h2>
      <p data-option-id="confirmed"><strong>Proceed with this policy</strong></p>
    </section>
  </article>`;

describe('Workbench inline decision markup', () => {
  it('discovers stable points while the source remains ordinary readable HTML', () => {
    const points = parseWorkbenchDecisionPoints(STATIC_HTML);

    expect(points).toEqual([
      expect.objectContaining({
        id: 'routing-owner',
        kind: 'single',
        label: 'Where should routing run?',
        commentEnabled: true,
        options: [
          { id: 'task-api', label: 'Task API' },
          { id: 'studio-only', label: 'Studio only' },
        ],
      }),
      expect.objectContaining({ id: 'policy-ready', kind: 'confirm' }),
    ]);
    expect(STATIC_HTML).not.toContain('<input');
    expect(STATIC_HTML).toContain('Editable recommendation.');
  });

  it('injects host-owned controls and restores persisted answers in the isolated wrapper', () => {
    const points = parseWorkbenchDecisionPoints(STATIC_HTML);
    const answers = normalizeWorkbenchDecisionAnswers(points, [
      {
        decisionId: 'routing-owner',
        kind: 'single',
        selectedOptionIds: ['task-api'],
        comment: 'Keep the override.',
      },
      {
        decisionId: 'policy-ready',
        kind: 'confirm',
        selectedOptionIds: ['confirmed'],
        comment: null,
      },
    ])!;

    const srcdoc = buildWorkbenchDecisionSrcdoc(STATIC_HTML, points, answers, true, 'dark');

    expect(srcdoc).toContain("'workbench-decision-' + point.id + '-' + optionId");
    expect(srcdoc).toContain('Keep the override.');
    expect(srcdoc).toContain(WORKBENCH_DECISION_CHANGE_MESSAGE);
    expect(srcdoc).toContain('disabled = config.disabled');
    expect(srcdoc).toContain('data-agent-studio-theme="dark"');
    expect(srcdoc).toContain('Editable recommendation.');
    expect(workbenchDecisionAnswersComplete(points, answers)).toBe(true);
  });

  it('rejects frame payload ids that were not declared by the source document', () => {
    const points = parseWorkbenchDecisionPoints(STATIC_HTML);

    expect(normalizeWorkbenchDecisionAnswers(points, [{
      decisionId: 'routing-owner',
      kind: 'single',
      selectedOptionIds: ['forged-option'],
      comment: null,
    }])).toBeNull();
  });
});

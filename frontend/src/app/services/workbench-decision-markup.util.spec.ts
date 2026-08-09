import { describe, expect, it } from 'vitest';
import {
  discoverWorkbenchDecisionMarkup,
  normalizeWorkbenchDecisionResponses,
} from './workbench-decision-markup.util';

describe('Workbench decision markup', () => {
  it('discovers readable single, multi, and comment markup with stable ids', () => {
    const discovered = discoverWorkbenchDecisionMarkup(`
      <section data-decision-id="route" data-decision-kind="single">
        <h3>Choose a route</h3>
        <ul>
          <li data-option-id="direct">Direct path</li>
          <li data-option-id="queue"><label><input type="radio" checked> Queue first</label></li>
        </ul>
        <label>Notes <textarea data-comment="Optional routing note">Keep it bounded.</textarea></label>
      </section>
      <section data-decision-id="checks" data-decision-kind="multi" data-decision-label="Required checks">
        <p data-option-id="build">Build</p><p data-option-id="e2e">Playwright</p>
      </section>`);

    expect(discovered.points).toEqual([
      {
        id: 'route', kind: 'single', label: 'Choose a route',
        options: [{ id: 'direct', label: 'Direct path' }, { id: 'queue', label: 'Queue first' }],
        commentLabel: 'Optional routing note',
      },
      {
        id: 'checks', kind: 'multi', label: 'Required checks',
        options: [{ id: 'build', label: 'Build' }, { id: 'e2e', label: 'Playwright' }],
        commentLabel: null,
      },
    ]);
    expect(discovered.responses[0]).toEqual({
      decisionId: 'route', kind: 'single', selectedOptionIds: ['queue'], comment: 'Keep it bounded.',
    });
  });

  it('ignores malformed duplicates and rejects frame state outside the discovered contract', () => {
    const discovered = discoverWorkbenchDecisionMarkup(`
      <p data-decision-id="route" data-decision-kind="single"><span data-option-id="a">A</span></p>
      <p data-decision-id="route" data-decision-kind="single"><span data-option-id="b">B</span></p>
      <p data-decision-id="bad id" data-decision-kind="single"><span data-option-id="a">A</span></p>`);

    expect(discovered.points).toHaveLength(1);
    expect(normalizeWorkbenchDecisionResponses([
      { decisionId: 'route', kind: 'single', selectedOptionIds: ['outside'], comment: null },
    ], discovered.points)).toBeNull();
    expect(normalizeWorkbenchDecisionResponses([
      { decisionId: 'route', kind: 'single', selectedOptionIds: ['a'], comment: 'Chosen in place.' },
    ], discovered.points)).toEqual([
      { decisionId: 'route', kind: 'single', selectedOptionIds: ['a'], comment: 'Chosen in place.' },
    ]);
  });
});

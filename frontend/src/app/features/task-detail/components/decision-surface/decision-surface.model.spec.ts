import { describe, expect, it } from 'vitest';
import {
  buildDecisionSubmission,
  parseDecisionJson,
  parseEmbeddedDecision,
} from './decision-surface.model';

const VALID_DECISION = {
  version: 1,
  id: 'icon-source',
  title: 'Choose the icon source',
  question: 'Which icon family should the new action use?',
  context: 'The choice affects the shared action row.',
  recommendation: {
    optionId: 'lucide',
    reason: 'It matches the existing outline language.',
  },
  options: [
    {
      id: 'lucide',
      label: 'Use Lucide',
      summary: 'Use the existing outline family.',
      consequences: ['Consistent visual language', 'No second icon dependency'],
      action: {
        kind: 'steer',
        prompt: 'Use Lucide and preserve the existing size tokens.',
      },
    },
    {
      id: 'keep-current',
      label: 'Keep current glyphs',
      summary: 'Accept the current implementation.',
      consequences: ['No additional implementation work'],
      action: {
        kind: 'move',
        targetState: '6-completed',
      },
    },
  ],
  steer: {
    label: 'Additional constraints',
    placeholder: 'Optional guidance',
    required: false,
  },
};

describe('decision-surface/v1 parser', () => {
  it('parses an allowlisted decision and its recommendation', () => {
    const result = parseDecisionJson(JSON.stringify(VALID_DECISION));

    expect(result.error).toBeNull();
    expect(result.document?.recommendation?.optionId).toBe('lucide');
    expect(result.document?.options[1].action).toEqual({
      kind: 'move',
      targetState: '6-completed',
    });
  });

  it('fails closed for a move target outside the operator allowlist', () => {
    const unsafe = structuredClone(VALID_DECISION);
    unsafe.options[1].action = {
      kind: 'move',
      targetState: '3-in-progress',
    };

    const result = parseDecisionJson(JSON.stringify(unsafe));

    expect(result.document).toBeNull();
    expect(result.error).toContain('not allowed');
  });

  it('extracts the contract from an inert decision HTML script', () => {
    const html = `
      <!doctype html>
      <html>
        <body>
          <h1>Icon comparison</h1>
          <script type="application/json" data-agent-studio-decision>
            ${JSON.stringify(VALID_DECISION)}
          </script>
        </body>
      </html>`;

    expect(parseEmbeddedDecision(html).document?.id).toBe('icon-source');
  });

  it('builds a journal reason and a steer prompt with the free-text guidance', () => {
    const decision = parseDecisionJson(JSON.stringify(VALID_DECISION)).document!;
    const submission = buildDecisionSubmission(
      decision,
      decision.options[0],
      'Keep the close icon at 16 px.',
      'results/decision.html',
    );

    expect(submission.reason).toContain('selected "Use Lucide"');
    expect(submission.reason).toContain('Keep the close icon at 16 px.');
    expect(submission.prompt).toContain('Use Lucide and preserve the existing size tokens.');
    expect(submission.prompt).toContain('Consequences acknowledged:');
    expect(submission.prompt).toContain('Additional operator guidance:');
  });
});

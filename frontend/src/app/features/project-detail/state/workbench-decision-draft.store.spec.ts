import { beforeEach, describe, expect, it } from 'vitest';
import { WorkbenchDecisionDraftStore } from './workbench-decision-draft.store';

describe('WorkbenchDecisionDraftStore', () => {
  beforeEach(() => sessionStorage.clear());

  it('restores fields and selected options from browser session storage', () => {
    const first = new WorkbenchDecisionDraftStore();
    first.saveResponses('Agent Studio', 'naming-dossier', [{
      decisionId: 'naming',
      kind: 'single',
      selectedOptionIds: ['stable-key'],
      comment: 'Keep the public key stable.',
    }]);
    first.beginFeature('Agent Studio', 'naming-dossier', {
      actor: 'Operator',
      title: 'Apply the naming decision',
      goal: 'Use the selected public naming contract.',
      operationId: 'workbench-ui-persisted',
    }, first.draft('Agent Studio', 'naming-dossier')!.responses);

    const restored = new WorkbenchDecisionDraftStore()
      .draft('Agent Studio', 'naming-dossier');

    expect(restored).toEqual(expect.objectContaining({
      mode: 'feature-spawn',
      title: 'Apply the naming decision',
      goal: 'Use the selected public naming contract.',
      operationId: 'workbench-ui-persisted',
      responses: [expect.objectContaining({ selectedOptionIds: ['stable-key'] })],
    }));
  });

  it('removes the persisted draft explicitly', () => {
    const drafts = new WorkbenchDecisionDraftStore();
    drafts.saveResponses('Agent Studio', 'naming-dossier', [{
      decisionId: 'naming',
      kind: 'single',
      selectedOptionIds: ['stable-key'],
      comment: null,
    }]);

    drafts.discard('Agent Studio', 'naming-dossier');

    expect(new WorkbenchDecisionDraftStore().draft('Agent Studio', 'naming-dossier')).toBeNull();
  });
});

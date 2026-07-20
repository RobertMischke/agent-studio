import { describe, expect, it } from 'vitest';
import type { CliModelInfo } from '../../cli';
import { availableCodexModels, resolveComposerSelection } from './orchestrator-composer-model.util';

const liveCatalog: CliModelInfo[] = [
  model('gpt-5.6-sol', ['minimal', 'low', 'medium', 'high', 'xhigh', 'ultra'], true),
  model('gpt-5.6-pro', ['low', 'medium', 'high', 'xhigh']),
  model('gpt-5.5', ['minimal', 'low', 'medium', 'high', 'xhigh']),
  model('gpt-5.4', ['low', 'medium', 'high']),
  model('gpt-5.4-mini', ['low', 'medium', 'high']),
  model('gpt-5.3-codex-spark', ['low', 'medium', 'high']),
];

function model(id: string, thinkingLevels: string[], isDefault = false): CliModelInfo {
  return {
    id,
    label: id.toUpperCase(),
    multiplier: null,
    vendor: 'openai',
    isDefault,
    available: true,
    deprecated: false,
    thinkingLevels,
    defaultThinkingLevel: thinkingLevels.at(-1) ?? null,
  };
}

describe('OrchestratorComposerModelService', () => {
  it('renders every available live GPT model exactly once without a maintained allow-list', () => {
    const advertisedDeprecated = { ...model('gpt-deprecated-but-available', ['low']), deprecated: true };
    const rendered = availableCodexModels([
      ...liveCatalog,
      liveCatalog[0],
      { ...model('gpt-retired', ['low']), available: false },
      model('non-gpt-catalog-entry', ['low']),
      advertisedDeprecated,
    ]).map(item => item.id);

    expect(rendered).toEqual([...liveCatalog.map(item => item.id), advertisedDeprecated.id]);
    expect(new Set(rendered).size).toBe(rendered.length);
  });

  it('switches reasoning ladders by model and keeps an explicit choice across consumers', () => {
    const selection = { cliType: 'codex', model: 'gpt-5.4-mini', thinkingLevel: 'low' };
    const effective = resolveComposerSelection(liveCatalog, selection, null, null);

    expect(effective).toEqual({
      cliType: 'codex', model: 'gpt-5.4-mini', thinkingLevel: 'low',
    });
    expect(liveCatalog.find(item => item.id === 'gpt-5.4-mini')?.thinkingLevels)
      .toEqual(['low', 'medium', 'high']);
    expect(liveCatalog.find(item => item.id === 'gpt-5.6-sol')?.thinkingLevels)
      .toEqual(['minimal', 'low', 'medium', 'high', 'xhigh', 'ultra']);
    expect(resolveComposerSelection(liveCatalog, effective, null, null).model)
      .toBe('gpt-5.4-mini');
  });

  it('labels the catalogue default as inherited and uses its advertised reasoning default', () => {
    expect(resolveComposerSelection(liveCatalog, null, null, null)).toEqual({
      cliType: 'codex', model: 'gpt-5.6-sol', thinkingLevel: 'ultra',
    });
  });
});

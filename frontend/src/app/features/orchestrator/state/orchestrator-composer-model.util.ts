import type { ChatModelSelection } from 'coding-agent-chat/core';
import type { CliModelInfo } from '../../cli';

export function availableCodexModels(models: readonly CliModelInfo[]): readonly CliModelInfo[] {
  const unique = new Map<string, CliModelInfo>();
  for (const model of models) {
    if (model.available !== false
      && model.id.toLowerCase().startsWith('gpt-')
      && !unique.has(model.id)) {
      unique.set(model.id, model);
    }
  }
  return [...unique.values()];
}

export function resolveComposerSelection(
  catalog: readonly CliModelInfo[],
  explicit: ChatModelSelection | null,
  inheritedModel: string | null,
  inheritedThinking: string | null,
): ChatModelSelection {
  const requested = explicit && catalog.some(model => model.id === explicit.model)
    ? explicit
    : {
        cliType: 'codex',
        model: catalog.find(item => item.id === inheritedModel)?.id
          ?? catalog.find(item => item.isDefault)?.id
          ?? catalog[0]?.id
          ?? '',
        thinkingLevel: inheritedThinking,
      };
  const model = catalog.find(item => item.id === requested.model);
  const levels = model?.thinkingLevels ?? [];
  return {
    cliType: 'codex',
    model: requested.model,
    thinkingLevel: requested.thinkingLevel && levels.includes(requested.thinkingLevel)
      ? requested.thinkingLevel
      : model?.defaultThinkingLevel ?? levels[0] ?? null,
  };
}

import { describe, it, expect } from 'vitest';
import {
  MODEL_MENU_DEFAULT_ID,
  buildModelMenuItems,
  cliTypeFromMenuId,
  currentBadgeText,
  isCliMenuId,
  isModelMenuId,
  modelBadgeTooltip,
  modelIdFromMenuId,
  shortModelName,
} from './model-badge-menu-builders';
import { CLI_TYPES, type CliType } from '../../../../../models/task.model';
import type { CliModelInfo } from '../../../../cli';
import type { MenuRow } from '../../../../../components/menu';

const labels: Record<CliType, string> = {
  claude: 'Claude Code',
  codex: 'Codex',
  copilot: 'Copilot',
  gemini: 'Gemini',
};
const icons: Record<CliType, string> = {
  claude: 'C',
  codex: 'X',
  copilot: 'P',
  gemini: 'G',
};

const models: CliModelInfo[] = [
  { id: 'claude-opus-4-7', label: 'Opus 4.7', multiplier: 5, vendor: 'anthropic', isDefault: true },
  { id: 'claude-sonnet-4-6', label: 'Sonnet 4.6', multiplier: 1, vendor: 'anthropic', isDefault: false },
];

describe('shortModelName', () => {
  it('shortens common claude ids to family + version', () => {
    expect(shortModelName('claude-opus-4-7')).toBe('opus 4.7');
    expect(shortModelName('claude-sonnet-4-6')).toBe('sonnet 4.6');
    expect(shortModelName('claude-haiku-4-5')).toBe('haiku 4.5');
  });

  it('keeps other ids verbatim except for stripping a vendor slash', () => {
    expect(shortModelName('gpt-4o')).toBe('gpt-4o');
    expect(shortModelName('openai/gpt-4o')).toBe('gpt-4o');
  });

  it('falls back to "No model" for empty / nullish input', () => {
    expect(shortModelName(null)).toBe('No model');
    expect(shortModelName(undefined)).toBe('No model');
    expect(shortModelName('   ')).toBe('No model');
  });
});

describe('currentBadgeText', () => {
  it('renders "{cli} · {model}" when both are set', () => {
    expect(
      currentBadgeText({
        cliType: 'claude',
        model: 'claude-opus-4-7',
        cliTypeLabel: (t) => labels[t],
      }),
    ).toBe('Claude Code · claude-opus-4-7');
  });

  it('falls back when the model is missing', () => {
    expect(
      currentBadgeText({
        cliType: 'claude',
        model: null,
        cliTypeLabel: (t) => labels[t],
      }),
    ).toBe('Claude Code · CLI default');
  });

  it('falls back when both are missing', () => {
    expect(
      currentBadgeText({ cliType: null, model: null, cliTypeLabel: (t) => labels[t] }),
    ).toBe('no CLI · CLI default');
  });
});

describe('modelBadgeTooltip', () => {
  it('appends a change hint when interactive', () => {
    const t = modelBadgeTooltip(
      { cliType: 'claude', model: 'claude-opus-4-7', cliTypeLabel: (t) => labels[t] },
      null,
    );
    expect(t).toContain('Claude Code · claude-opus-4-7');
    expect(t).toContain('click or right-click');
  });

  it('appends the disabled reason in place of the change hint', () => {
    const t = modelBadgeTooltip(
      { cliType: 'claude', model: 'claude-opus-4-7', cliTypeLabel: (t) => labels[t] },
      'Stop the run first.',
    );
    expect(t).toContain('Stop the run first.');
    expect(t).not.toContain('right-click');
  });
});

describe('buildModelMenuItems', () => {
  function build(overrides: Partial<Parameters<typeof buildModelMenuItems>[0]> = {}) {
    return buildModelMenuItems({
      cliType: 'claude',
      model: 'claude-opus-4-7',
      availableModels: models,
      cliTypes: CLI_TYPES,
      cliTypeLabel: (t) => labels[t],
      cliTypeIcon: (t) => icons[t],
      ...overrides,
    });
  }

  it('marks the current model row as active', () => {
    const items = build();
    const opus = items.find(
      (i): i is MenuRow => i.kind === 'row' && i.id === 'model:claude-opus-4-7',
    );
    const sonnet = items.find(
      (i): i is MenuRow => i.kind === 'row' && i.id === 'model:claude-sonnet-4-6',
    );
    expect(opus?.active).toBe(true);
    expect(sonnet?.active).toBe(false);
  });

  it('marks the CLI default row active when no model is set', () => {
    const items = build({ model: null });
    const def = items.find((i): i is MenuRow => i.kind === 'row' && i.id === MODEL_MENU_DEFAULT_ID);
    expect(def?.active).toBe(true);
  });

  it('marks the current CLI row as active', () => {
    const items = build({ cliType: 'codex' });
    const codex = items.find((i): i is MenuRow => i.kind === 'row' && i.id === 'cli:codex');
    const claude = items.find((i): i is MenuRow => i.kind === 'row' && i.id === 'cli:claude');
    expect(codex?.active).toBe(true);
    expect(claude?.active).toBe(false);
  });

  it('still emits the CLI section when the model catalog is empty', () => {
    const items = build({ availableModels: [] });
    const cliRows = items.filter((i): i is MenuRow => i.kind === 'row' && i.id.startsWith('cli:'));
    expect(cliRows.length).toBe(CLI_TYPES.length);
  });

  it('always includes the CLI default row even with an empty catalog', () => {
    const items = build({ availableModels: [] });
    const def = items.find((i): i is MenuRow => i.kind === 'row' && i.id === MODEL_MENU_DEFAULT_ID);
    expect(def).toBeDefined();
  });
});

describe('menu-id routing helpers', () => {
  it('detects model vs cli ids', () => {
    expect(isModelMenuId('model:claude-opus-4-7')).toBe(true);
    expect(isModelMenuId(MODEL_MENU_DEFAULT_ID)).toBe(true);
    expect(isCliMenuId('cli:claude')).toBe(true);
    expect(isModelMenuId('cli:claude')).toBe(false);
  });

  it('extracts the model id (default → empty string)', () => {
    expect(modelIdFromMenuId('model:claude-opus-4-7')).toBe('claude-opus-4-7');
    expect(modelIdFromMenuId(MODEL_MENU_DEFAULT_ID)).toBe('');
    expect(modelIdFromMenuId('cli:claude')).toBeNull();
  });

  it('extracts the CLI type', () => {
    expect(cliTypeFromMenuId('cli:claude')).toBe('claude');
    expect(cliTypeFromMenuId('model:foo')).toBeNull();
  });
});

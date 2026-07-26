import { describe, expect, it } from 'vitest';
import type { CliModelInfo } from './cli.model';
import { orderModelCatalog } from './model-catalog-ordering';

function model(id: string, overrides: Partial<CliModelInfo> = {}): CliModelInfo {
  return {
    id,
    label: id,
    multiplier: null,
    vendor: null,
    isDefault: false,
    available: true,
    deprecated: false,
    ...overrides,
  };
}

describe('orderModelCatalog', () => {
  it('sorts leading family generations first and older generations newest-first', () => {
    const ordered = orderModelCatalog([
      model('claude-opus-4-7'),
      model('claude-sonnet-4-6'),
      model('claude-opus-5'),
      model('claude-opus-4-8'),
      model('claude-sonnet-5'),
    ]);

    expect(ordered.map((item) => item.id)).toEqual([
      'claude-opus-5',
      'claude-sonnet-5',
      'claude-opus-4-8',
      'claude-opus-4-7',
      'claude-sonnet-4-6',
    ]);
  });

  it('marks superseded generations deprecated with an older-generation note', () => {
    const ordered = orderModelCatalog([
      model('claude-opus-4-7'),
      model('claude-opus-5'),
      model('claude-opus-4-8'),
    ]);

    expect(ordered[0]).toMatchObject({ id: 'claude-opus-5', deprecated: false });
    expect(ordered[1]).toMatchObject({
      id: 'claude-opus-4-8',
      deprecated: true,
      availabilityNote: 'Older generation',
    });
    expect(ordered[2]).toMatchObject({
      id: 'claude-opus-4-7',
      deprecated: true,
      availabilityNote: 'Older generation',
    });
  });

  it('preserves explicit catalog notes and keeps every available model selectable', () => {
    const input = [
      model('gpt-5.5', { deprecated: true, availabilityNote: 'Superseded by GPT-5.6.' }),
      model('gpt-5.6-sol'),
      model('gpt-5.4-mini'),
    ];

    const ordered = orderModelCatalog(input);

    expect(ordered).toHaveLength(input.length);
    expect(ordered.every((item) => item.available !== false)).toBe(true);
    expect(ordered.find((item) => item.id === 'gpt-5.5')).toMatchObject({
      deprecated: true,
      availabilityNote: 'Superseded by GPT-5.6.',
    });
  });

  it('places genuinely unavailable entries last without making older entries unavailable', () => {
    const ordered = orderModelCatalog([
      model('claude-opus-4-7'),
      model('claude-opus-5'),
      model('claude-opus-4-1', { available: false, deprecated: true }),
    ]);

    expect(ordered.map((item) => item.id)).toEqual([
      'claude-opus-5',
      'claude-opus-4-7',
      'claude-opus-4-1',
    ]);
    expect(ordered[1].available).toBe(true);
    expect(ordered[2].available).toBe(false);
  });

  it('keeps discovery order for ids without a conventional numeric generation', () => {
    const ordered = orderModelCatalog([
      model('claude-latest'),
      model('claude-stable'),
    ]);

    expect(ordered.map((item) => item.id)).toEqual(['claude-latest', 'claude-stable']);
  });
});

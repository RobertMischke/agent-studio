import { describe, expect, it } from 'vitest';
import {
  aspectVerdictLabel,
  aspectVerdictTone,
  parseAspectFindings,
  parseFindingsJson,
  resolveAspectFindings,
} from './aspect-findings.model';

describe('aspectVerdictTone', () => {
  it('maps the canonical tokens onto severity tones', () => {
    expect(aspectVerdictTone('pass')).toBe('ok');
    expect(aspectVerdictTone('concerns')).toBe('warn');
    expect(aspectVerdictTone('block')).toBe('danger');
  });

  it('tolerates the spelling drift the backend parser accepts', () => {
    expect(aspectVerdictTone('concern')).toBe('warn');
    expect(aspectVerdictTone('blocked')).toBe('danger');
    expect(aspectVerdictTone('  PASS ')).toBe('ok');
  });

  it('falls back to neutral for unknown / empty tokens', () => {
    expect(aspectVerdictTone('whatever')).toBe('neutral');
    expect(aspectVerdictTone('')).toBe('neutral');
    expect(aspectVerdictTone(null)).toBe('neutral');
    expect(aspectVerdictTone(undefined)).toBe('neutral');
  });
});

describe('aspectVerdictLabel', () => {
  it('normalises drifted spellings onto a stable token', () => {
    expect(aspectVerdictLabel('concern')).toBe('concerns');
    expect(aspectVerdictLabel('blocked')).toBe('block');
    expect(aspectVerdictLabel('PASS')).toBe('pass');
  });

  it('keeps an unknown token but defaults blanks to "finding"', () => {
    expect(aspectVerdictLabel('flaky')).toBe('flaky');
    expect(aspectVerdictLabel('')).toBe('finding');
    expect(aspectVerdictLabel(null)).toBe('finding');
  });
});

describe('parseAspectFindings (legacy blob fallback)', () => {
  it('parses the **{aspect}** [{verdict}]: {reason} bullet shape', () => {
    const blob = [
      '- **requirement-fit** [concerns]: missing edge-case test',
      '- **code-quality** [block]: helper duplicated across modules',
    ].join('\n');
    expect(parseAspectFindings(blob)).toEqual([
      { aspect: 'requirement-fit', verdict: 'concerns', reason: 'missing edge-case test' },
      { aspect: 'code-quality', verdict: 'block', reason: 'helper duplicated across modules' },
    ]);
  });

  it('parses lines without bullets or bold markers', () => {
    expect(parseAspectFindings('security [pass]: no new attack surface')).toEqual([
      { aspect: 'security', verdict: 'pass', reason: 'no new attack surface' },
    ]);
  });

  it('lower-cases the verdict token and skips non-matching lines', () => {
    const blob = [
      'GAP:',
      '- **tests** [BLOCK]: no coverage',
      'this is a free-form sentence with no token',
    ].join('\n');
    expect(parseAspectFindings(blob)).toEqual([
      { aspect: 'tests', verdict: 'block', reason: 'no coverage' },
    ]);
  });

  it('returns [] for a plain reason string with no [token]', () => {
    expect(parseAspectFindings('just a sentence about why it reopened')).toEqual([]);
    expect(parseAspectFindings('')).toEqual([]);
    expect(parseAspectFindings(null)).toEqual([]);
    expect(parseAspectFindings(undefined)).toEqual([]);
  });
});

describe('parseFindingsJson (structured path)', () => {
  it('parses the camelCase JSON array the backend writes', () => {
    const json = JSON.stringify([
      { aspect: 'requirement-fit', verdict: 'concerns', reason: 'missing test' },
      { aspect: 'code-quality', verdict: 'block', reason: 'dup helper' },
    ]);
    expect(parseFindingsJson(json)).toEqual([
      { aspect: 'requirement-fit', verdict: 'concerns', reason: 'missing test' },
      { aspect: 'code-quality', verdict: 'block', reason: 'dup helper' },
    ]);
  });

  it('drops rows without an aspect and defaults missing fields', () => {
    const json = JSON.stringify([
      { aspect: 'tests', verdict: 'block' },
      { verdict: 'concerns', reason: 'orphan' },
    ]);
    expect(parseFindingsJson(json)).toEqual([
      { aspect: 'tests', verdict: 'block', reason: '' },
    ]);
  });

  it('returns [] for malformed / non-array / empty JSON', () => {
    expect(parseFindingsJson('not json')).toEqual([]);
    expect(parseFindingsJson('{"aspect":"x"}')).toEqual([]);
    expect(parseFindingsJson('')).toEqual([]);
    expect(parseFindingsJson(null)).toEqual([]);
  });
});

describe('resolveAspectFindings (structured-first, parse-fallback)', () => {
  it('prefers the structured JSON over the legacy blob', () => {
    const json = JSON.stringify([{ aspect: 'a', verdict: 'pass', reason: 'ok' }]);
    const blob = '- **b** [block]: legacy';
    expect(resolveAspectFindings(json, blob)).toEqual([
      { aspect: 'a', verdict: 'pass', reason: 'ok' },
    ]);
  });

  it('falls back to parsing the blob when structured JSON is absent', () => {
    const blob = '- **b** [block]: legacy';
    expect(resolveAspectFindings(null, blob)).toEqual([
      { aspect: 'b', verdict: 'block', reason: 'legacy' },
    ]);
  });

  it('returns [] when neither source yields findings', () => {
    expect(resolveAspectFindings(null, 'plain reason text')).toEqual([]);
  });
});

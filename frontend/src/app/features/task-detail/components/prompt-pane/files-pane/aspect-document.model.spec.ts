import { describe, expect, it } from 'vitest';
import { parseAspectDocument } from './aspect-document.model';

describe('parseAspectDocument', () => {
  const valid = JSON.stringify({
    schemaVersion: 1,
    aspect: 'code-quality',
    status: 'concerns',
    summary: 'Dead helper left behind.',
    details: 'The new file duplicates `foo()`.',
    createdAt: '2026-07-09T19:21:03Z',
    model: 'claude-haiku-4-5',
    tag: 'quality:concerns',
  });

  it('parses a well-formed structured aspect document', () => {
    const doc = parseAspectDocument(valid);
    expect(doc).not.toBeNull();
    expect(doc!.aspect).toBe('code-quality');
    expect(doc!.status).toBe('concerns');
    expect(doc!.summary).toBe('Dead helper left behind.');
    expect(doc!.details).toContain('duplicates');
    expect(doc!.model).toBe('claude-haiku-4-5');
    expect(doc!.tag).toBe('quality:concerns');
  });

  it('lower-cases the status token so tone mapping is stable', () => {
    const doc = parseAspectDocument(JSON.stringify({ aspect: 'x', status: 'BLOCK', summary: '', details: '' }));
    expect(doc!.status).toBe('block');
  });

  it('returns null for a legacy markdown aspect file (not JSON)', () => {
    const md = '---\naspect: code-quality\nstatus: pass\n---\n\n# Aspect';
    expect(parseAspectDocument(md)).toBeNull();
  });

  it('returns null for empty / null / whitespace input', () => {
    expect(parseAspectDocument('')).toBeNull();
    expect(parseAspectDocument(null)).toBeNull();
    expect(parseAspectDocument('   ')).toBeNull();
  });

  it('returns null for a JSON array or a payload missing the load-bearing fields', () => {
    expect(parseAspectDocument('[]')).toBeNull();
    expect(parseAspectDocument(JSON.stringify({ status: 'pass' }))).toBeNull(); // no aspect
    expect(parseAspectDocument(JSON.stringify({ aspect: 'x' }))).toBeNull(); // no status
  });

  it('returns null for malformed JSON', () => {
    expect(parseAspectDocument('{ not valid json')).toBeNull();
  });

  it('collects string/number/boolean metrics and drops an empty map', () => {
    const withMetrics = parseAspectDocument(
      JSON.stringify({
        aspect: 'tests-and-evidence',
        status: 'pass',
        summary: 'ok',
        details: '',
        metrics: { filesChanged: 3, testsPassed: '157', green: true, nested: { skip: 1 } },
      }),
    );
    expect(withMetrics!.metrics).toEqual({ filesChanged: '3', testsPassed: '157', green: 'true' });

    const noMetrics = parseAspectDocument(
      JSON.stringify({ aspect: 'x', status: 'pass', summary: '', details: '', metrics: {} }),
    );
    expect(noMetrics!.metrics).toBeNull();
  });
});

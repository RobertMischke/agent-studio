import { describe, expect, it } from 'vitest';
import { mergeByTimestamp } from './merge-by-timestamp';

interface T {
  id: string;
  timestamp: string;
  source: 'msg' | 'evt';
}

const m = (id: string, timestamp: string): T => ({ id, timestamp, source: 'msg' });
const e = (id: string, timestamp: string): T => ({ id, timestamp, source: 'evt' });

describe('mergeByTimestamp', () => {
  it('returns the secondary stream untouched when primary is empty', () => {
    expect(mergeByTimestamp<T>([], [e('e1', '2026-01-01T00:00:00Z')])).toEqual([
      e('e1', '2026-01-01T00:00:00Z')
    ]);
  });

  it('returns the primary stream untouched when secondary is empty', () => {
    expect(mergeByTimestamp<T>([m('m1', '2026-01-01T00:00:00Z')], [])).toEqual([
      m('m1', '2026-01-01T00:00:00Z')
    ]);
  });

  it('interleaves two streams in chronological order', () => {
    const messages = [
      m('m1', '2026-01-01T00:00:00Z'),
      m('m2', '2026-01-01T00:02:00Z'),
      m('m3', '2026-01-01T00:05:00Z')
    ];
    const events = [
      e('e1', '2026-01-01T00:01:00Z'),
      e('e2', '2026-01-01T00:03:00Z'),
      e('e3', '2026-01-01T00:04:00Z')
    ];
    expect(mergeByTimestamp(messages, events).map((x) => x.id)).toEqual([
      'm1',
      'e1',
      'm2',
      'e2',
      'e3',
      'm3'
    ]);
  });

  it('keeps primary before secondary on equal timestamps', () => {
    // Tie-break behaviour: messages-first, events-second. Documented
    // contract; the chat body is sticky-to-bottom, so a near-simultaneous
    // tool-call event firing alongside an orchestrator turn renders the
    // turn first and the event card right under it.
    const ts = '2026-01-01T00:00:00Z';
    const out = mergeByTimestamp([m('m1', ts)], [e('e1', ts)]);
    expect(out.map((x) => x.id)).toEqual(['m1', 'e1']);
  });

  it('preserves relative input order within the same stream', () => {
    // Two events emitted in the same millisecond must keep their input
    // order; otherwise out-of-order ticks could surface in the chat.
    const ts = '2026-01-01T00:00:00Z';
    const events = [e('e1', ts), e('e2', ts), e('e3', ts)];
    expect(mergeByTimestamp([], events).map((x) => x.id)).toEqual(['e1', 'e2', 'e3']);
  });

  it('handles long primary tail after secondary exhausts', () => {
    const messages = [
      m('m1', '2026-01-01T00:00:00Z'),
      m('m2', '2026-01-01T00:02:00Z'),
      m('m3', '2026-01-01T00:03:00Z'),
      m('m4', '2026-01-01T00:04:00Z')
    ];
    const events = [e('e1', '2026-01-01T00:01:00Z')];
    expect(mergeByTimestamp(messages, events).map((x) => x.id)).toEqual([
      'm1',
      'e1',
      'm2',
      'm3',
      'm4'
    ]);
  });
});

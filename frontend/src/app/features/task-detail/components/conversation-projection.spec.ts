// Regression coverage for the host-side conversation-projection guard.
//
// The guard is the app's defense-in-depth against raw stream-json transport
// frames leaking into the chat (bug: "Activity-Log zeigt rohes JSON statt
// Nutzer-Nachrichten"). These specs pin a CATALOG of current stream-json event
// shapes - including thinking blocks with a signature, tool_use, and the
// several tool_result forms - and assert that:
//   1. every transport frame is redacted to the "[internal event]" marker,
//   2. the raw JSON survives on `internalDetail` (disclosable, never inline),
//   3. a brand-new / unknown event shape does NOT fall back to raw rendering,
//   4. ordinary prose and non-transport JSON snippets are left untouched, and
//   5. Codex JSONL frames (owned by the library) are never redacted.
//
// This spec imports only the guard + the app model, so it runs standalone
// without the coding-agent-chat library.
import { describe, expect, it } from 'vitest';
import {
  INTERNAL_EVENT_MARKER,
  isInternalEventLine,
  isNonRenderableRawLine,
  isRenderableActivityKind,
  sanitizeProjectionLines,
} from './conversation-projection';
import { CliOutputLine } from '../../../models/task.model';

function line(text: string, stream = 'stdout', timestamp = '2026-07-09T03:15:00.000Z'): CliOutputLine {
  return { timestamp, stream, text };
}

// A catalog of raw stream-json frames as they appear on a single stdout line
// when the backend renderer did not (or could not) turn them into a marker.
const STREAM_JSON_CATALOG: readonly { name: string; raw: string }[] = [
  {
    name: 'assistant text frame',
    raw: '{"type":"assistant","message":{"id":"msg_1","role":"assistant","content":[{"type":"text","text":"hello world"}]}}',
  },
  {
    name: 'assistant tool_use frame',
    raw: '{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"a.ts"}}]}}',
  },
  {
    name: 'thinking block with signature',
    raw: '{"type":"thinking","thinking":"Let me reason about this.","signature":"Er8BCkgI...=="}',
  },
  {
    name: 'user message wrapping a tool_result (string content)',
    raw: '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"file body"}]}}',
  },
  {
    name: 'user message wrapping a tool_result (array content, is_error)',
    raw: '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_2","is_error":true,"content":[{"type":"text","text":"boom"}]}]}}',
  },
  {
    name: 'bare tool_result frame',
    raw: '{"type":"tool_result","tool_use_id":"toolu_3","content":[{"type":"text","text":"ok"}]}',
  },
  {
    name: 'system init frame',
    raw: '{"type":"system","subtype":"init","session_id":"sess-abc","tools":["Read","Bash"]}',
  },
  {
    name: 'result frame',
    raw: '{"type":"result","subtype":"success","is_error":false,"result":"done","usage":{"input_tokens":10}}',
  },
  {
    name: 'rate_limit_event frame',
    raw: '{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1777393800}}',
  },
  {
    name: 'content_block_delta streaming frame',
    raw: '{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"partial"}}',
  },
  {
    name: 'bare content-block array',
    raw: '[{"type":"text","text":"a"},{"type":"tool_use","id":"t","name":"Read","input":{}}]',
  },
  {
    name: 'message envelope without an explicit type',
    raw: '{"role":"assistant","content":[{"type":"text","text":"hi"}]}',
  },
];

// The forward-compat case: an event type this code has never seen. It must be
// redacted (it still carries transport structure), never rendered raw.
const UNKNOWN_FUTURE_FRAMES: readonly { name: string; raw: string }[] = [
  {
    name: 'unknown future event type carrying a message envelope',
    raw: '{"type":"web_search_tool_result","message":{"role":"assistant","content":[{"type":"text","text":"x"}]}}',
  },
  {
    name: 'unknown future event type carrying a content array',
    raw: '{"type":"server_tool_use","content":[{"type":"text","text":"y"}],"stop_reason":"end_turn"}',
  },
  {
    name: 'unknown future event type carrying a signature',
    raw: '{"type":"redacted_reasoning","signature":"AbCd=="}',
  },
];

describe('isNonRenderableRawLine — stream-json catalog', () => {
  for (const { name, raw } of STREAM_JSON_CATALOG) {
    it(`flags a ${name}`, () => {
      expect(isNonRenderableRawLine(raw)).toBe(true);
    });
  }

  for (const { name, raw } of UNKNOWN_FUTURE_FRAMES) {
    it(`flags an ${name} (no raw fallback for new shapes)`, () => {
      expect(isNonRenderableRawLine(raw)).toBe(true);
    });
  }
});

describe('isNonRenderableRawLine — false-positive guard', () => {
  const RENDERABLE: readonly { name: string; text: string }[] = [
    { name: 'plain agent prose', text: 'Looking at the activity-log component now.' },
    { name: 'a Claude marker line', text: '● Read prompt.md' },
    { name: 'a tool action line', text: '* Edit src/foo.ts' },
    { name: 'a non-transport JSON config snippet', text: '{"port":5030,"host":"localhost"}' },
    { name: 'a JSON snippet with an unrelated type', text: '{"type":"chart","value":42}' },
    { name: 'a prose line that merely mentions JSON', text: 'The frame was {"type":"assistant"} shaped.' },
    { name: 'an envelope with a non-message role', text: '{"role":"admin","content":"x"}' },
    { name: 'an empty object', text: '{}' },
    { name: 'a bare number-array', text: '[1,2,3]' },
    { name: 'an incomplete/partial JSON fragment', text: '{"type":"assistant",' },
  ];
  for (const { name, text } of RENDERABLE) {
    it(`does not flag ${name}`, () => {
      expect(isNonRenderableRawLine(text)).toBe(false);
    });
  }

  it('leaves Codex JSONL frames for the library to parse', () => {
    expect(isNonRenderableRawLine('{"type":"item.completed","item":{"type":"command_execution","id":"c1","command":"ls","exit_code":0}}')).toBe(false);
    expect(isNonRenderableRawLine('{"type":"item.started","item":{"type":"agent_message","text":"hi"}}')).toBe(false);
    expect(isNonRenderableRawLine('{"type":"turn.completed","usage":{"input_tokens":1}}')).toBe(false);
  });
});

describe('sanitizeProjectionLines', () => {
  it('replaces every raw frame with the compact marker and preserves the raw JSON', () => {
    const raw = STREAM_JSON_CATALOG[0].raw;
    const [out] = sanitizeProjectionLines([line(raw)]);
    expect(out.text).toBe(INTERNAL_EVENT_MARKER);
    expect(out.internalDetail).toBe(raw);
    expect(isInternalEventLine(out)).toBe(true);
  });

  it('never emits raw stream-json as visible text for any catalog shape', () => {
    const lines = STREAM_JSON_CATALOG.map((c) => line(c.raw));
    for (const out of sanitizeProjectionLines(lines)) {
      expect(out.text).toBe(INTERNAL_EVENT_MARKER);
      expect(out.text).not.toContain('"type"');
      expect(out.text).not.toContain('{');
    }
  });

  it('collapses a run of consecutive frames into one marker with joined detail', () => {
    const a = STREAM_JSON_CATALOG[0].raw;
    const b = STREAM_JSON_CATALOG[1].raw;
    const out = sanitizeProjectionLines([line(a), line(b)]);
    expect(out).toHaveLength(1);
    expect(out[0].text).toBe(INTERNAL_EVENT_MARKER);
    expect(out[0].internalDetail).toContain(a);
    expect(out[0].internalDetail).toContain(b);
  });

  it('keeps real messages between frames intact and starts a fresh marker each run', () => {
    const frame = STREAM_JSON_CATALOG[2].raw;
    const out = sanitizeProjectionLines([
      line(frame),
      line('real agent message'),
      line(frame),
    ]);
    expect(out.map((l) => l.text)).toEqual([
      INTERNAL_EVENT_MARKER,
      'real agent message',
      INTERNAL_EVENT_MARKER,
    ]);
  });

  it('preserves user and orchestrator lines verbatim', () => {
    const user = line('please continue', 'user');
    const orch = line('[reissue] follow-up', 'orchestrator');
    const out = sanitizeProjectionLines([user, orch]);
    expect(out[0]).toBe(user);
    expect(out[1]).toBe(orch);
  });

  it('returns the same array reference when nothing needs redacting (no churn)', () => {
    const input = [line('hello'), line('● Read a.ts')];
    expect(sanitizeProjectionLines(input)).toBe(input);
  });
});

describe('renderable-kind whitelist', () => {
  it('accepts every known activity-log kind', () => {
    for (const kind of ['read', 'search', 'command', 'edit', 'task', 'todo', 'error', 'message', 'orchestrator', 'supervisor', 'other']) {
      expect(isRenderableActivityKind(kind)).toBe(true);
    }
  });

  it('rejects an unknown kind', () => {
    expect(isRenderableActivityKind('stream-json')).toBe(false);
    expect(isRenderableActivityKind('')).toBe(false);
  });
});

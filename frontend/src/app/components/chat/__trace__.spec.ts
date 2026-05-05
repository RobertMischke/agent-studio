import { describe, it } from 'vitest';
import { testFailRetryFragment, toolBurstFragment } from './conversation-projection.fixtures';
import { projectConversation } from './conversation-projection';
import { parseActivityLog } from '../activity-log.parser';

describe('trace dump', () => {
  it('dumps', () => {
    const lines = testFailRetryFragment();
    const groups = parseActivityLog([...lines]);
    console.log('GROUPS:');
    for (const g of groups) console.log(g.kind, g.status, JSON.stringify(g.title), 'lines=', g.lines.length);
    const events = projectConversation({ source: 'X', lines, emitWorkbenchSummary: true });
    console.log('EVENTS:');
    for (const e of events) console.log(e.kind, JSON.stringify({ count: (e as any).count, failures: (e as any).failures, families: (e as any).families }));
  });
});

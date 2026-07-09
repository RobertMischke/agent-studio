import { describe, expect, it } from 'vitest';
import {
  parseBugHashtags,
  resolveAttachmentUrl,
  suppressLocalDuplicates,
} from './orchestrator-side-sheet.util';
import type { OrchestratorChatTurn } from '../../../../features/orchestrator';

/**
 * Direct coverage for the pure helpers extracted out of the side-sheet
 * controller (MC-2 controller split). These used to be module-private
 * functions exercised only through the component; pulling them into a
 * util file made them unit-addressable, so pin them here.
 */
describe('orchestrator-side-sheet.util', () => {
  describe('parseBugHashtags', () => {
    it('collects hashtags at the start of a line and skips markdown headings', () => {
      expect(parseBugHashtags('#frontend #ux\nsome detail')).toEqual(['frontend', 'ux']);
      // A "# " heading (space after hash) is not a tag.
      expect(parseBugHashtags('# Heading\nbody')).toEqual([]);
    });

    it('dedupes repeated tags and ignores mid-line hashes', () => {
      expect(parseBugHashtags('#bug\ntext with #notatag inline\n#bug')).toEqual(['bug']);
    });
  });

  describe('resolveAttachmentUrl', () => {
    it('routes a chat-attachments path through the per-project GET endpoint', () => {
      expect(resolveAttachmentUrl('demo', 'chat-attachments/shot.png')).toBe(
        '/api/runner/demo/orchestrator-chat/attachments/shot.png',
      );
    });

    it('returns the input unchanged when project or path is missing', () => {
      expect(resolveAttachmentUrl(null, 'chat-attachments/x.png')).toBe('chat-attachments/x.png');
      expect(resolveAttachmentUrl('demo', '')).toBe('');
    });
  });

  describe('suppressLocalDuplicates', () => {
    const turn = (id: string, text: string): OrchestratorChatTurn => ({
      id,
      ts: '2026-07-08T00:00:00Z',
      role: 'user',
      text,
    });

    it('hides the server user turn that a live local turn already represents', () => {
      const server = [turn('s1', 'hello')];
      const local = [turn('l1', 'hello')];
      expect(suppressLocalDuplicates(server, local).map((t) => t.id)).toEqual([]);
    });

    it('keeps server turns that no local turn matches', () => {
      const server = [turn('s1', 'hello'), turn('s2', 'world')];
      const local = [turn('l1', 'hello')];
      expect(suppressLocalDuplicates(server, local).map((t) => t.id)).toEqual(['s2']);
    });
  });
});

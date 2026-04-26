import { describe, expect, it } from 'vitest';
import { markdownToHtml } from './markdown-utils';

describe('markdownToHtml', () => {
  it('renders headings, lists and inline formatting', () => {
    expect(markdownToHtml('# Status\n\n- **Done**\n- `jobId`')).toBe(
      '<h1>Status</h1><ul><li><strong>Done</strong></li><li><code>jobId</code></li></ul>'
    );
  });

  it('escapes raw html before rendering markdown', () => {
    expect(markdownToHtml('Hello <script>alert(1)</script>')).toBe(
      '<p>Hello &lt;script&gt;alert(1)&lt;/script&gt;</p>'
    );
  });
});

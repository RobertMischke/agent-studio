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

  it('renders standalone image lines as block-level <img>', () => {
    expect(markdownToHtml('Before\n\n![shot](attachments/abc.png)\n\nAfter')).toBe(
      '<p>Before</p><img src="attachments/abc.png" alt="shot"><p>After</p>'
    );
  });

  it('expands attachment refs through resolveImageSrc', () => {
    const html = markdownToHtml('![shot](attachments/abc.png)', {
      resolveImageSrc: (src) =>
        src.startsWith('attachments/') ? `/api/jobs/x/${src}` : src
    });
    expect(html).toBe('<img src="/api/jobs/x/attachments/abc.png" alt="shot">');
  });

  it('renders ordered lists as <ol>', () => {
    expect(markdownToHtml('1. one\n2. two')).toBe(
      '<ol><li>one</li><li>two</li></ol>'
    );
  });

  it('renders inline links and treats javascript: URLs as unsafe', () => {
    const safe = markdownToHtml('See [docs](https://example.com).');
    expect(safe).toContain('href="https://example.com"');
    expect(safe).toContain('rel="noopener noreferrer"');

    const unsafe = markdownToHtml('[click](javascript:alert(1))');
    expect(unsafe).not.toContain('javascript:');
    expect(unsafe).toContain('href="#"');
  });

  it('treats single-asterisk pairs as italic alongside underscore italic', () => {
    expect(markdownToHtml('a *star* and _under_')).toBe(
      '<p>a <em>star</em> and <em>under</em></p>'
    );
  });
});

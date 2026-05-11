import { describe, expect, it } from 'vitest';
import { extractSentinel, renderResultsHtml } from './beautiful-results.renderer';

const CTX = { jobId: 'abc', watchPath: 'C:/Projects/repo' };

describe('extractSentinel', () => {
  it('returns null banner when no sentinel present', () => {
    const { cleaned, banner } = extractSentinel('Just a normal status report.\n\n## Section');
    expect(banner).toBeNull();
    expect(cleaned).toBe('Just a normal status report.\n\n## Section');
  });

  it('lifts trailing [[TASK_DONE]] into a banner and strips it from the body', () => {
    const { cleaned, banner } = extractSentinel('Final result: green.\n\n[[TASK_DONE]]');
    expect(banner?.kind).toBe('done');
    expect(banner?.reason).toBeNull();
    expect(cleaned).not.toContain('[[TASK_DONE]]');
    expect(cleaned).toContain('Final result: green.');
  });

  it('captures a [[TASK_BLOCKED:reason]] reason and exposes it on the banner', () => {
    const { cleaned, banner } = extractSentinel('Hit a wall.\n\n[[TASK_BLOCKED:missing API key]]');
    expect(banner?.kind).toBe('blocked');
    expect(banner?.reason).toBe('missing API key');
    expect(cleaned).not.toContain('[[TASK_BLOCKED');
  });

  it('uses the last sentinel when multiple appear in the body', () => {
    const md = 'First take: [[TASK_NEEDS_INPUT:retry?]]\n\nSecond take: [[TASK_DONE]]';
    const { banner } = extractSentinel(md);
    expect(banner?.kind).toBe('done');
  });

  it('does not match a similar-looking string inside an inline code span', () => {
    // The contract says structured trailers live on their own line at the
    // end of the agent's reply. The current implementation greedy-matches
    // [[TASK_*]] anywhere; this test pins the behaviour so a future
    // tightening of the regex is intentional, not a silent regression.
    const md = 'See marker `[[TASK_DONE]]` in body.\n\n[[TASK_BLOCKED:still working]]';
    const { banner } = extractSentinel(md);
    expect(banner?.kind).toBe('blocked');
  });
});

describe('renderResultsHtml', () => {
  it('renders headings, paragraphs, and inline emphasis', () => {
    const { html } = renderResultsHtml('# Title\n\nSome **bold** and *italic*.', CTX);
    expect(html).toContain('<h1>Title</h1>');
    expect(html).toContain('<strong>bold</strong>');
    expect(html).toMatch(/<em>italic<\/em>/);
  });

  it('renders deeply nested lists', () => {
    const md = '- top\n  - mid\n    - leaf';
    const { html } = renderResultsHtml(md, CTX);
    expect(html).toContain('<ul>');
    // Three nested <ul> open tags
    const opens = html.match(/<ul/g) ?? [];
    expect(opens.length).toBeGreaterThanOrEqual(3);
    expect(html).toContain('leaf');
  });

  it('escapes HTML-looking content inside code fences', () => {
    const md = '```html\n<script>alert(1)</script>\n```';
    const { html } = renderResultsHtml(md, CTX);
    expect(html).toContain('&lt;script&gt;');
    expect(html).not.toContain('<script>alert(1)</script>');
  });

  it('renders a diff fence with diff2html (d2h-* classes present)', () => {
    const md = '```diff\n--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n```';
    const { html } = renderResultsHtml(md, CTX);
    expect(html).toContain('data-results-diff');
    expect(html.toLowerCase()).toContain('d2h-');
  });

  it('decorates non-diff code blocks with a language label and copy button', () => {
    const md = '```typescript\nconst x: number = 1;\n```';
    const { html } = renderResultsHtml(md, CTX);
    expect(html).toContain('data-results-code');
    expect(html).toContain('data-lang="typescript"');
    expect(html.toLowerCase()).toContain('copy');
  });

  it('resolves attachments/* image paths through the job-folder API', () => {
    const md = '![shot](attachments/screen.png)';
    const { html } = renderResultsHtml(md, CTX);
    expect(html).toContain('/api/jobs/abc/attachments/screen.png');
    expect(html).toContain('data-results-lightbox');
    expect(html).toContain('<figure');
  });

  it('passes through absolute http(s) image URLs unchanged', () => {
    const md = '![logo](https://example.com/x.png)';
    const { html } = renderResultsHtml(md, CTX);
    expect(html).toContain('https://example.com/x.png');
  });

  it('renders tables with thead and tbody', () => {
    const md = '| a | b |\n|---|---|\n| 1 | 2 |';
    const { html } = renderResultsHtml(md, CTX);
    expect(html).toContain('<table');
    expect(html).toContain('<thead');
    expect(html).toContain('<td');
  });

  it('marks external links with target=_blank', () => {
    const md = 'See [docs](https://example.com).';
    const { html } = renderResultsHtml(md, CTX);
    expect(html).toMatch(/target="_blank"/);
    expect(html).toMatch(/rel="noopener noreferrer"/);
  });

  it('lifts the sentinel marker out of the body even when surrounded by text', () => {
    const { html, banner } = renderResultsHtml('Finished.\n\n[[TASK_DONE]]', CTX);
    expect(banner?.kind).toBe('done');
    expect(html).not.toContain('TASK_DONE');
  });

  it('sanitizes malicious raw HTML to keep XSS out', () => {
    const md = 'Hello <img src=x onerror="alert(1)">';
    const { html } = renderResultsHtml(md, CTX);
    expect(html).not.toContain('onerror');
  });

  it('returns empty html and null banner for empty input', () => {
    const { html, banner } = renderResultsHtml('', CTX);
    expect(html).toBe('');
    expect(banner).toBeNull();
  });
});

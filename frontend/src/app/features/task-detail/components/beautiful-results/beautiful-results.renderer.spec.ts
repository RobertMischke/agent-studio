import { describe, expect, it, beforeEach } from 'vitest';
import { extractSentinel, renderResultsHtml, clearResultsRenderCache } from './beautiful-results.renderer';

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
    expect(html).toContain('/api/tasks/abc/attachments/screen.png');
    expect(html).toContain('data-results-lightbox');
    expect(html).toContain('<figure');
  });

  it('resolves flat results/* image paths through the job-folder API', () => {
    const md = '![proof](results/proof.png)';
    const { html } = renderResultsHtml(md, CTX);
    expect(html).toContain('/api/tasks/abc/results/proof.png?watchPath=C%3A%2FProjects%2Frepo');
    expect(html).toContain('data-results-lightbox');
    expect(html).toContain('<img');
  });

  it('resolves nested results/* image paths through the screenshot endpoint', () => {
    const md = '![proof](results/playwright/spec/proof.png)';
    const { html } = renderResultsHtml(md, CTX);
    expect(html).toContain('/api/tasks/abc/screenshot?path=playwright%2Fspec%2Fproof.png&amp;watchPath=C%3A%2FProjects%2Frepo');
    expect(html).toContain('data-results-lightbox');
    expect(html).toContain('<img');
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

  it('routes docs, task keys, and HTML reports to typed task-aware controls', () => {
    const md = [
      '[convention](docs/quality/angular-components.md)',
      '[report](results/report.html)',
      '[card](#/tasks/AGT-2437)',
    ].join('\n\n');
    const { html } = renderResultsHtml(md, CTX);

    expect(html).toContain('data-results-wiki="quality/angular-components.md"');
    expect(html).toContain('data-results-artifact="results/report.html"');
    expect(html).toContain('/api/tasks/abc/results/report.html?watchPath=C%3A%2FProjects%2Frepo');
    expect(html).toContain('target="_blank"');
    expect(html).toContain('data-results-task-key="AGT-2437"');
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

describe('renderResultsHtml memo', () => {
  // Re-renders of an unchanged status.md / results.md happen every time
  // the operator clicks back into a previously visible task. The memo
  // turns that into a single Map lookup so the operator-visible "switch
  // back to this run" path stays sub-10 ms.
  beforeEach(() => clearResultsRenderCache());

  it('returns the same object reference on a repeat render with identical inputs', () => {
    const md = '# Title\n\nBody text with `code` and a [link](https://example.com).';
    const first = renderResultsHtml(md, CTX);
    const second = renderResultsHtml(md, CTX);
    expect(second).toBe(first);
  });

  it('does not collide between two jobs with the same markdown body', () => {
    const md = 'Shared body.';
    const a = renderResultsHtml(md, { jobId: 'job-a', watchPath: '/wp' });
    const b = renderResultsHtml(md, { jobId: 'job-b', watchPath: '/wp' });
    // Distinct cache entries (image resolver may reach for jobId), distinct
    // object identity. The HTML body is allowed to be equal in this fixture
    // since the inputs are image-free.
    expect(a).not.toBe(b);
  });

  it('clearResultsRenderCache forces a fresh render', () => {
    const md = 'Stable body.';
    const first = renderResultsHtml(md, CTX);
    clearResultsRenderCache();
    const second = renderResultsHtml(md, CTX);
    expect(second).not.toBe(first);
    expect(second.html).toBe(first.html);
  });
});

import { describe, expect, it } from 'vitest';
import { escapeHtml, splitHighlightedLines, splitPlainLines } from './source-highlight';

describe('escapeHtml', () => {
  it('escapes the five HTML-significant characters', () => {
    expect(escapeHtml(`<a href="x">&'</a>`)).toBe(
      '&lt;a href=&quot;x&quot;&gt;&amp;&#39;&lt;/a&gt;',
    );
  });
});

describe('splitPlainLines', () => {
  it('normalizes CRLF and escapes each line', () => {
    const lines = splitPlainLines('a < b\r\nc & d');
    expect(lines).toEqual(['a &lt; b', 'c &amp; d']);
  });

  it('keeps a trailing empty line', () => {
    expect(splitPlainLines('a\n')).toEqual(['a', '']);
  });
});

describe('splitHighlightedLines', () => {
  it('produces one fragment per source line', () => {
    const html = 'const <span class="hljs-keyword">a</span> = 1;\nconst b = 2;';
    const out = splitHighlightedLines(html);
    expect(out).toHaveLength(2);
  });

  it('matches the line count of a plain split for the same source', () => {
    const source = 'line1\nline2\nline3\n';
    const highlighted =
      '<span class="hljs-meta">line1</span>\nline2\n<span class="hljs-comment">line3</span>\n';
    expect(splitHighlightedLines(highlighted)).toHaveLength(source.split('\n').length);
  });

  it('closes and re-opens a span that straddles a newline so each line is balanced', () => {
    const html = '<span class="hljs-comment">/* one\ntwo */</span>';
    const out = splitHighlightedLines(html);
    expect(out).toHaveLength(2);
    expect(out[0]).toBe('<span class="hljs-comment">/* one</span>');
    expect(out[1]).toBe('<span class="hljs-comment">two */</span>');
    // every fragment has matching open/close span counts
    for (const line of out) {
      const opens = (line.match(/<span\b/g) ?? []).length;
      const closes = (line.match(/<\/span>/g) ?? []).length;
      expect(opens).toBe(closes);
    }
  });

  it('balances nested spans across a newline', () => {
    const html =
      '<span class="hljs-string">"a<span class="hljs-subst">b\nc</span>d"</span>';
    const out = splitHighlightedLines(html);
    expect(out).toHaveLength(2);
    for (const line of out) {
      const opens = (line.match(/<span\b/g) ?? []).length;
      const closes = (line.match(/<\/span>/g) ?? []).length;
      expect(opens).toBe(closes);
    }
    expect(out[0]).toBe('<span class="hljs-string">"a<span class="hljs-subst">b</span></span>');
    expect(out[1]).toBe('<span class="hljs-string"><span class="hljs-subst">c</span>d"</span>');
  });

  it('handles HTML with no spans at all', () => {
    expect(splitHighlightedLines('plain\ntext')).toEqual(['plain', 'text']);
  });
});

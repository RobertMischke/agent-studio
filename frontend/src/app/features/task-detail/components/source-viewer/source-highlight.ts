/**
 * Line-splitting helpers for the source viewer.
 *
 * highlight.js returns a single HTML string for the whole file (so multi-line
 * constructs like block comments / template strings keep their context). To
 * render line numbers we split that HTML on newlines while keeping every open
 * `<span>` balanced per line: any spans still open at a line break are closed
 * and re-opened on the next line. The line count therefore matches a plain
 * `content.split('\n')`, so the gutter stays aligned with the code.
 */

export function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

/** Plain (un-highlighted) lines: normalize EOLs, split, HTML-escape each. */
export function splitPlainLines(text: string): string[] {
  return text.replace(/\r\n/g, '\n').split('\n').map(escapeHtml);
}

/**
 * Split highlight.js output into per-line HTML fragments, re-balancing any
 * spans that straddle a newline. The result has the same length as
 * `text.split('\n')` for the same source.
 */
export function splitHighlightedLines(html: string): string[] {
  const lines: string[] = [];
  const stack: string[] = []; // opening <span ...> tags currently in effect
  let current = '';

  const openTags = () => stack.join('');
  const closeTags = () => stack.map(() => '</span>').join('');

  const pushText = (text: string): void => {
    const parts = text.split('\n');
    for (let i = 0; i < parts.length; i++) {
      if (i > 0) {
        current += closeTags();
        lines.push(current);
        current = openTags();
      }
      current += parts[i];
    }
  };

  const tagRe = /<\/?[^>]+>/g;
  let lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = tagRe.exec(html)) !== null) {
    if (m.index > lastIndex) pushText(html.slice(lastIndex, m.index));
    const tag = m[0];
    if (tag.startsWith('</')) {
      stack.pop();
    } else if (!tag.endsWith('/>')) {
      stack.push(tag);
    }
    current += tag;
    lastIndex = tagRe.lastIndex;
  }
  if (lastIndex < html.length) pushText(html.slice(lastIndex));
  lines.push(current);
  return lines;
}

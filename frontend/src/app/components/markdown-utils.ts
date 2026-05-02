/**
 * Optional URL transformers for image sources. The prompt editor renders
 * `attachments/<file>` references as full API URLs while editing, then
 * collapses them back when serializing so prompt.md on disk keeps the
 * relative path the CLI agent expects.
 */
export interface MarkdownImageOptions {
  resolveImageSrc?: (mdSrc: string) => string;
  serializeImageSrc?: (htmlSrc: string) => string;
}

// Sentinel placeholders wrap image and link tokens so the bold/italic regex
// can't chew their innards. We use long random strings rather than control
// chars to keep the source file plain ASCII; collisions with real escaped
// agent text are vanishingly unlikely.
const IMG_OPEN = 'XmdImgOpenA93f4X';
const IMG_CLOSE = 'XmdImgCloseA93f4X';
const LINK_OPEN = 'XmdLnkOpenA93f4X';
const LINK_CLOSE = 'XmdLnkCloseA93f4X';
const CODE_OPEN = 'XmdCodeOpenA93f4X';
const CODE_CLOSE = 'XmdCodeCloseA93f4X';

export function markdownToHtml(markdown: string, options: MarkdownImageOptions = {}): string {
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const html: string[] = [];
  let paragraph: string[] = [];
  let listItems: string[] = [];
  let orderedItems: string[] = [];
  let inCode = false;
  let codeLines: string[] = [];

  const flushParagraph = () => {
    if (paragraph.length === 0) return;
    html.push(`<p>${formatInline(paragraph.join(' '), options)}</p>`);
    paragraph = [];
  };

  const flushList = () => {
    if (listItems.length === 0) return;
    html.push(`<ul>${listItems.map((item) => `<li>${formatInline(item, options)}</li>`).join('')}</ul>`);
    listItems = [];
  };

  const flushOrderedList = () => {
    if (orderedItems.length === 0) return;
    html.push(`<ol>${orderedItems.map((item) => `<li>${formatInline(item, options)}</li>`).join('')}</ol>`);
    orderedItems = [];
  };

  for (const rawLine of lines) {
    const line = rawLine.trimEnd();

    if (line.startsWith('```')) {
      if (inCode) {
        html.push(`<pre><code>${escapeHtml(codeLines.join('\n'))}</code></pre>`);
        codeLines = [];
        inCode = false;
      } else {
        flushParagraph();
        flushList();
        flushOrderedList();
        inCode = true;
      }
      continue;
    }

    if (inCode) {
      codeLines.push(rawLine);
      continue;
    }

    if (!line.trim()) {
      flushParagraph();
      flushList();
      flushOrderedList();
      continue;
    }

    const heading = /^(#{1,4})\s+(.+)$/.exec(line);
    if (heading) {
      flushParagraph();
      flushList();
      flushOrderedList();
      const level = heading[1].length;
      html.push(`<h${level}>${formatInline(heading[2], options)}</h${level}>`);
      continue;
    }

    const list = /^[-*]\s+(.+)$/.exec(line);
    if (list) {
      flushParagraph();
      flushOrderedList();
      listItems.push(list[1]);
      continue;
    }

    const ordered = /^\d+\.\s+(.+)$/.exec(line);
    if (ordered) {
      flushParagraph();
      flushList();
      orderedItems.push(ordered[1]);
      continue;
    }

    // Standalone image line — render as a block-level <img> so the editor
    // shows the screenshot on its own row instead of wrapping it in <p>.
    const blockImage = /^!\[([^\]]*)\]\(([^)\s]+)\)\s*$/.exec(line);
    if (blockImage) {
      flushParagraph();
      flushList();
      flushOrderedList();
      html.push(renderImage(blockImage[1], blockImage[2], options));
      continue;
    }

    flushList();
    flushOrderedList();
    paragraph.push(line.trim());
  }

  if (inCode) {
    html.push(`<pre><code>${escapeHtml(codeLines.join('\n'))}</code></pre>`);
  }
  flushParagraph();
  flushList();
  flushOrderedList();

  return html.join('');
}

export function htmlToMarkdown(html: string, options: MarkdownImageOptions = {}): string {
  const doc = new DOMParser().parseFromString(html, 'text/html');
  const blocks: string[] = [];

  for (const child of Array.from(doc.body.childNodes)) {
    const markdown = nodeToMarkdown(child, options).trimEnd();
    if (markdown) {
      blocks.push(markdown);
    }
  }

  return blocks.join('\n\n').trimEnd();
}

function nodeToMarkdown(node: ChildNode, options: MarkdownImageOptions): string {
  if (node.nodeType === Node.TEXT_NODE) {
    return (node.textContent ?? '').replace(/\s+/g, ' ');
  }

  if (!(node instanceof HTMLElement)) {
    return '';
  }

  const tag = node.tagName.toLowerCase();
  const children = () => Array.from(node.childNodes).map((c) => nodeToMarkdown(c, options)).join('');

  switch (tag) {
    case 'h1':
      return `# ${children().trim()}`;
    case 'h2':
      return `## ${children().trim()}`;
    case 'h3':
      return `### ${children().trim()}`;
    case 'h4':
      return `#### ${children().trim()}`;
    case 'p':
      return children().trim();
    case 'strong':
    case 'b':
      return `**${children().trim()}**`;
    case 'em':
    case 'i':
      return `_${children().trim()}_`;
    case 'code':
      if (node.parentElement?.tagName.toLowerCase() === 'pre') {
        return node.textContent ?? '';
      }
      return `\`${node.textContent ?? ''}\``;
    case 'pre':
      return `\`\`\`\n${node.textContent ?? ''}\n\`\`\``;
    case 'ul':
      return Array.from(node.children).map((child) => `- ${nodeToMarkdown(child, options).trim()}`).join('\n');
    case 'ol':
      return Array.from(node.children).map((child, i) => `${i + 1}. ${nodeToMarkdown(child, options).trim()}`).join('\n');
    case 'li':
      return children().trim();
    case 'br':
      return '\n';
    case 'a': {
      const href = (node as HTMLAnchorElement).getAttribute('href') ?? '';
      const label = children().trim();
      return href ? `[${label}](${href})` : label;
    }
    case 'img': {
      const src = (node as HTMLImageElement).getAttribute('src') ?? '';
      const alt = (node as HTMLImageElement).getAttribute('alt') ?? '';
      const serialized = options.serializeImageSrc ? options.serializeImageSrc(src) : src;
      return `![${alt}](${serialized})`;
    }
    default:
      return children();
  }
}

function formatInline(value: string, options: MarkdownImageOptions): string {
  // Order matters here: extract structured spans (images, links, inline code)
  // from the *raw* input before HTML-escaping. Two reasons:
  //
  //  1. URLs inside [..](..) must not be double-escaped. Past bug:
  //     `[x](https://e.com/?a=1&b=2)` produced `&amp;amp;` because the input
  //     was escapeHtml'd first (turning `&` into `&amp;`) and then the URL
  //     was escapeAttribute'd again inside renderLink.
  //  2. Inline code spans (`MAX_LINE_LENGTH`) must not be touched by the
  //     bold/italic regex. Past bug: the underscore-italic regex matched
  //     across the code span boundary and rendered `MAX<em>LINE</em>LENGTH`.
  //
  // Each extraction stores the rendered HTML on the side and replaces the
  // original span with a unique sentinel token. Once bold/italic/escape are
  // done, we splice the rendered HTML back in.
  const images: string[] = [];
  const links: string[] = [];
  const codes: string[] = [];

  // Image first because `![..](..)` is a superset of `[..](..)`.
  let stripped = value.replace(/!\[([^\]]*)\]\(([^)\s]+)\)/g, (_full, alt, src) => {
    images.push(renderImage(alt, src, options));
    return `${IMG_OPEN}${images.length - 1}${IMG_CLOSE}`;
  });
  // Then plain links.
  stripped = stripped.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_full, label, url) => {
    links.push(renderLink(label, url));
    return `${LINK_OPEN}${links.length - 1}${LINK_CLOSE}`;
  });
  // Then inline code spans. The contents are HTML-escaped at render time so
  // they survive innerHTML safely; we hold them as already-rendered HTML
  // strings until the splice step.
  stripped = stripped.replace(/`([^`]+)`/g, (_full, body: string) => {
    codes.push(`<code>${escapeHtml(body)}</code>`);
    return `${CODE_OPEN}${codes.length - 1}${CODE_CLOSE}`;
  });

  // Now safely escape the residue (no <a>, <img>, <code> tokens left to mangle).
  const escaped = escapeHtml(stripped);

  const formatted = escaped
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/__([^_]+)__/g, '<strong>$1</strong>')
    .replace(/_([^_]+)_/g, '<em>$1</em>')
    .replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>');

  return formatted
    .replace(/XmdImgOpenA93f4X(\d+)XmdImgCloseA93f4X/g, (_full, idx) => images[Number(idx)] ?? '')
    .replace(/XmdLnkOpenA93f4X(\d+)XmdLnkCloseA93f4X/g, (_full, idx) => links[Number(idx)] ?? '')
    .replace(/XmdCodeOpenA93f4X(\d+)XmdCodeCloseA93f4X/g, (_full, idx) => codes[Number(idx)] ?? '');
}

function renderLink(label: string, url: string): string {
  const safe = safeLinkUrl(url);
  // Label is raw user-supplied text — must be HTML-escaped for the inner-text
  // position. URL goes into an attribute so it gets the attribute escape.
  return `<a href="${escapeAttribute(safe)}" target="_blank" rel="noopener noreferrer">${escapeHtml(label)}</a>`;
}

/**
 * Allow only http(s):, mailto:, and relative URLs in links. Anything else
 * (javascript:, data:, vbscript:, ...) collapses to '#' so a malicious agent
 * cannot smuggle a click handler through a fenced link in chat output.
 */
function safeLinkUrl(raw: string): string {
  const trimmed = raw.trim();
  if (!trimmed) return '#';
  if (/^(https?:|mailto:)/i.test(trimmed)) return trimmed;
  if (/^[/.#]/.test(trimmed)) return trimmed;
  if (/^[a-z0-9][a-z0-9+.-]*:/i.test(trimmed)) return '#';
  return trimmed;
}

function renderImage(alt: string, src: string, options: MarkdownImageOptions): string {
  const resolved = options.resolveImageSrc ? options.resolveImageSrc(src) : src;
  return `<img src="${escapeAttribute(resolved)}" alt="${escapeAttribute(alt)}">`;
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function escapeAttribute(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

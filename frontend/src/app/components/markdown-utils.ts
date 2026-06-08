import { Marked, type MarkedExtension, type Tokens } from 'marked';
import DOMPurify from 'dompurify';

/**
 * Optional URL transformers for image sources. The prompt editor renders
 * `attachments/<file>` references as full API URLs while editing, then
 * collapses them back when serializing so prompt.md on disk keeps the
 * relative path the CLI agent expects.
 */
export interface MarkdownImageOptions {
  resolveImageSrc?: (mdSrc: string) => string;
  serializeImageSrc?: (htmlSrc: string) => string;
  /**
   * Render fenced code blocks with a numbered gutter when the block has
   * more than `codeLineNumberThreshold` lines (default 5). Off by default
   * so the editor's HTML <-> markdown round-trip stays byte-stable; the
   * chat surface opts in for the dev-tools-leaning look on long blocks.
   */
  codeLineNumbers?: boolean;
  codeLineNumberThreshold?: number;
  taskReferences?: readonly MarkdownTaskReference[];
}

export interface MarkdownTaskReference {
  label: string;
  taskKey: string;
}

export function markdownToHtml(markdown: string, options: MarkdownImageOptions = {}): string {
  if (!markdown) return '';
  const local = new Marked(buildMarkedExtension(options));
  try {
    const parsed = local.parse(markdown);
    const html = linkTaskReferencesInHtml(typeof parsed === 'string' ? parsed : '', options.taskReferences);
    return compactHtml(sanitizeHtml(html));
  } catch {
    return `<pre><code>${escapeHtml(markdown)}</code></pre>`;
  }
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

function buildMarkedExtension(options: MarkdownImageOptions): MarkedExtension {
  return {
    gfm: true,
    breaks: false,
    renderer: {
      code(token: Tokens.Code): string {
        const lang = (token.lang ?? '').trim().split(/\s+/, 1)[0]?.toLowerCase() || null;
        return renderCodeBlock(token.text ?? '', lang, options);
      },
      html(token: Tokens.HTML | Tokens.Tag): string {
        return escapeHtml(token.text ?? token.raw ?? '');
      },
      paragraph(token: Tokens.Paragraph): string {
        const inline = this.parser.parseInline(token.tokens);
        const standaloneImage = token.tokens.length === 1 && token.tokens[0]?.type === 'image';
        return standaloneImage ? inline : `<p>${inline}</p>`;
      },
      link(token: Tokens.Link): string {
        const href = safeLinkUrl(token.href ?? '');
        const inner = token.tokens && token.tokens.length
          ? this.parser.parseInline(token.tokens)
          : escapeHtml(token.text ?? '');
        return `<a href="${escapeAttribute(href)}" target="_blank" rel="noopener noreferrer">${inner}</a>`;
      },
      image(token: Tokens.Image): string {
        return renderImage(token.text ?? '', token.href ?? '', token.title ?? null, options);
      },
    },
  };
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

function renderCodeBlock(source: string, lang: string | null, options: MarkdownImageOptions): string {
  const codeLines = source.split('\n');
  const threshold = options.codeLineNumberThreshold ?? 5;
  const hasLang = !!lang;
  const langAttrs = hasLang ? ` data-lang="${escapeAttribute(lang!)}"` : '';
  // Only attach md-code* classes when a language is present, otherwise
  // keep the historical `<pre><code>` shape (pinned by spec tests +
  // any downstream consumer that grep'd on the literal markup).
  if (!options.codeLineNumbers || codeLines.length <= threshold) {
    if (!hasLang) {
      return `<pre><code>${escapeHtml(source)}</code></pre>`;
    }
    const langClass = ` md-code--lang-${escapeAttribute(normaliseLang(lang!))}`;
    return `<pre class="md-code${langClass}"${langAttrs}><code>${escapeHtml(source)}</code></pre>`;
  }
  // Numbered shape: one row per source line, gutter cells get a stable
  // class so the chat stylesheet can hide them from text selection.
  const rows = codeLines
    .map((line, i) => {
      const num = i + 1;
      return `<span class="md-code-row" data-line="${num}">`
        + `<span class="md-code-num" aria-hidden="true">${num}</span>`
        + `<span class="md-code-text">${escapeHtml(line)}</span>`
        + `</span>`;
    })
    .join('');
  const langClass = hasLang ? ` md-code--lang-${escapeAttribute(normaliseLang(lang!))}` : '';
  return `<pre class="md-code md-code--numbered${langClass}" data-line-count="${codeLines.length}"${langAttrs}><code>${rows}</code></pre>`;
}

/**
 * Map common Claude / Codex fence labels to a small canonical set so
 * the CSS only needs one rule per family (e.g. `ts` + `tsx` + `typescript`
 * all collapse to `ts`).
 */
function normaliseLang(lang: string): string {
  switch (lang) {
    case 'typescript': case 'tsx': return 'ts';
    case 'javascript': case 'jsx': case 'mjs': case 'cjs': return 'js';
    case 'python': return 'py';
    case 'shell': case 'sh': case 'zsh': return 'bash';
    case 'yml': return 'yaml';
    case 'csharp': case 'cs': return 'csharp';
    case 'powershell': case 'ps': case 'ps1': return 'powershell';
    case 'patch': return 'diff';
    case 'plaintext': case 'text': case 'txt': return 'text';
    default: return lang.replace(/[^a-z0-9]/g, '');
  }
}

function renderImage(alt: string, src: string, _title: string | null, options: MarkdownImageOptions): string {
  const resolved = options.resolveImageSrc ? options.resolveImageSrc(src) : src;
  return `<img src="${escapeAttribute(resolved)}" alt="${escapeAttribute(alt)}">`;
}

export function sanitizeHtml(raw: string): string {
  if (typeof window === 'undefined' || typeof document === 'undefined') {
    return raw;
  }
  return DOMPurify.sanitize(raw, {
    USE_PROFILES: { html: true },
    ADD_ATTR: [
      'target',
      'rel',
      'title',
      'data-line-count',
      'data-line',
      'data-lang',
      'data-task-ref',
      'data-task-key',
      'aria-hidden',
    ],
    FORBID_TAGS: ['style', 'iframe', 'object', 'embed', 'script'],
    FORBID_ATTR: ['onerror', 'onload', 'onclick'],
  });
}

export function linkTaskReferencesInHtml(
  html: string,
  references: readonly MarkdownTaskReference[] | null | undefined,
): string {
  if (!html || !references?.length || typeof document === 'undefined') return html;
  const unique = uniqueTaskReferences(references);
  if (!unique.length) return html;
  const byLabel = new Map(unique.map(ref => [ref.label.toLowerCase(), ref]));
  const pattern = new RegExp(`(^|[^A-Za-z0-9_-])(${unique.map(ref => escapeRegExp(ref.label)).join('|')})(?=$|[^A-Za-z0-9_-])`, 'gi');
  const template = document.createElement('template');
  template.innerHTML = html;
  const walker = document.createTreeWalker(template.content, NodeFilter.SHOW_TEXT, {
    acceptNode(node) {
      const parent = node.parentElement;
      if (!parent) return NodeFilter.FILTER_REJECT;
      if (parent.closest('a, code, pre, kbd, samp, script, style')) return NodeFilter.FILTER_REJECT;
      pattern.lastIndex = 0;
      return pattern.test(node.textContent ?? '')
        ? NodeFilter.FILTER_ACCEPT
        : NodeFilter.FILTER_REJECT;
    },
  });

  const nodes: Text[] = [];
  while (walker.nextNode()) nodes.push(walker.currentNode as Text);
  for (const node of nodes) {
    pattern.lastIndex = 0;
    const fragment = document.createDocumentFragment();
    let cursor = 0;
    let match: RegExpExecArray | null;
    const text = node.textContent ?? '';
    while ((match = pattern.exec(text)) !== null) {
      const prefix = match[1] ?? '';
      const label = match[2] ?? '';
      const labelStart = match.index + prefix.length;
      const ref = byLabel.get(label.toLowerCase());
      if (!ref) continue;
      if (labelStart > cursor) fragment.append(document.createTextNode(text.slice(cursor, labelStart)));
      const anchor = document.createElement('a');
      anchor.href = `#task:${encodeURIComponent(ref.taskKey)}`;
      anchor.dataset['taskRef'] = 'true';
      anchor.dataset['taskKey'] = ref.taskKey;
      anchor.textContent = text.slice(labelStart, labelStart + label.length);
      fragment.append(anchor);
      cursor = labelStart + label.length;
    }
    if (cursor < text.length) fragment.append(document.createTextNode(text.slice(cursor)));
    node.replaceWith(fragment);
  }
  return template.innerHTML;
}

function uniqueTaskReferences(references: readonly MarkdownTaskReference[]): MarkdownTaskReference[] {
  const byLabel = new Map<string, MarkdownTaskReference>();
  const duplicateLabels = new Set<string>();
  for (const ref of references) {
    const label = ref.label.trim();
    if (!label || !ref.taskKey) continue;
    const key = label.toLowerCase();
    if (byLabel.has(key)) {
      duplicateLabels.add(key);
      continue;
    }
    byLabel.set(key, { label, taskKey: ref.taskKey });
  }
  for (const duplicate of duplicateLabels) byLabel.delete(duplicate);
  return [...byLabel.values()].sort((a, b) => b.label.length - a.label.length);
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function compactHtml(html: string): string {
  return html.trim().replace(/>\s+</g, '><');
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

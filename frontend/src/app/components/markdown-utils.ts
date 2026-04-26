export function markdownToHtml(markdown: string): string {
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const html: string[] = [];
  let paragraph: string[] = [];
  let listItems: string[] = [];
  let inCode = false;
  let codeLines: string[] = [];

  const flushParagraph = () => {
    if (paragraph.length === 0) return;
    html.push(`<p>${formatInline(paragraph.join(' '))}</p>`);
    paragraph = [];
  };

  const flushList = () => {
    if (listItems.length === 0) return;
    html.push(`<ul>${listItems.map((item) => `<li>${formatInline(item)}</li>`).join('')}</ul>`);
    listItems = [];
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
      continue;
    }

    const heading = /^(#{1,4})\s+(.+)$/.exec(line);
    if (heading) {
      flushParagraph();
      flushList();
      const level = heading[1].length;
      html.push(`<h${level}>${formatInline(heading[2])}</h${level}>`);
      continue;
    }

    const list = /^[-*]\s+(.+)$/.exec(line);
    if (list) {
      flushParagraph();
      listItems.push(list[1]);
      continue;
    }

    flushList();
    paragraph.push(line.trim());
  }

  if (inCode) {
    html.push(`<pre><code>${escapeHtml(codeLines.join('\n'))}</code></pre>`);
  }
  flushParagraph();
  flushList();

  return html.join('');
}

export function htmlToMarkdown(html: string): string {
  const doc = new DOMParser().parseFromString(html, 'text/html');
  const blocks: string[] = [];

  for (const child of Array.from(doc.body.childNodes)) {
    const markdown = nodeToMarkdown(child).trimEnd();
    if (markdown) {
      blocks.push(markdown);
    }
  }

  return blocks.join('\n\n').trimEnd();
}

function nodeToMarkdown(node: ChildNode): string {
  if (node.nodeType === Node.TEXT_NODE) {
    return (node.textContent ?? '').replace(/\s+/g, ' ');
  }

  if (!(node instanceof HTMLElement)) {
    return '';
  }

  const tag = node.tagName.toLowerCase();
  const children = () => Array.from(node.childNodes).map(nodeToMarkdown).join('');

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
      return Array.from(node.children).map((child) => `- ${nodeToMarkdown(child).trim()}`).join('\n');
    case 'li':
      return children().trim();
    case 'br':
      return '\n';
    default:
      return children();
  }
}

function formatInline(value: string): string {
  return escapeHtml(value)
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/_([^_]+)_/g, '<em>$1</em>');
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

export interface WikiLinkedElement {
  label: string;
  target: string;
  kind: 'doc' | 'anchor' | 'external' | 'task';
  taskReference: string | null;
}

/** Extract explicit Markdown/HTML links for the document's meta-panel index. */
export function extractWikiLinkedElements(content: string): WikiLinkedElement[] {
  const links: WikiLinkedElement[] = [];
  const seen = new Set<string>();
  const push = (label: string, target: string): void => {
    const cleanTarget = target.trim();
    if (!cleanTarget || cleanTarget.startsWith('mailto:')) return;
    const cleanLabel = label.trim() || cleanTarget;
    const key = `${cleanLabel}\u0000${cleanTarget}`;
    if (seen.has(key)) return;
    seen.add(key);
    const taskReference = taskReferenceFrom(cleanLabel, cleanTarget);
    links.push({
      label: cleanLabel,
      target: cleanTarget,
      kind: taskReference ? 'task' : linkKind(cleanTarget),
      taskReference,
    });
  };

  const markdownLink = /(!)?\[([^\]]+)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;
  for (const match of content.matchAll(markdownLink)) {
    if (!match[1]) push(match[2] ?? '', match[3] ?? '');
  }

  const htmlLink = /<a\s+[^>]*href=["']([^"']+)["'][^>]*>(.*?)<\/a>/gis;
  for (const match of content.matchAll(htmlLink)) {
    push((match[2] ?? '').replace(/<[^>]+>/g, '').trim(), match[1] ?? '');
  }

  return links.slice(0, 8);
}

/** Resolve a docs-relative link against the currently open wiki page. */
export function resolveWikiPageTarget(target: string, openedRel: string): string | null {
  let path = target.split(/[?#]/, 1)[0]?.trim() ?? '';
  if (!path) return null;
  try {
    path = decodeURIComponent(path);
  } catch {
    return null;
  }
  path = path.replaceAll('\\', '/');
  const absolute = path.startsWith('/');
  path = path.replace(/^\/+/, '').replace(/^docs\//, '');
  const parts = absolute ? [] : parentDir(openedRel).split('/').filter(Boolean);
  for (const part of path.split('/')) {
    if (!part || part === '.') continue;
    if (part === '..') parts.pop();
    else parts.push(part);
  }
  return parts.join('/') || null;
}

export function wikiLinkedElementKindLabel(kind: WikiLinkedElement['kind']): string {
  switch (kind) {
    case 'anchor': return 'Anchor';
    case 'external': return 'External';
    case 'task': return 'Task';
    default: return 'Page';
  }
}

export function wikiLinkedElementTitle(link: WikiLinkedElement): string {
  switch (link.kind) {
    case 'anchor': return `Jump to ${link.label}`;
    case 'external': return `Open external link: ${link.label}`;
    case 'task': return `Open task ${link.taskReference ?? link.label}`;
    default: return `Open wiki page: ${link.label}`;
  }
}

export function wikiAnchorId(target: string): string | null {
  const rawId = target.trim().replace(/^#/, '');
  if (!rawId) return null;
  try {
    return decodeURIComponent(rawId);
  } catch {
    return null;
  }
}

/** Find an id in the rendered page, including nested open ShadowRoots. */
export function findWikiAnchor(root: ParentNode, target: string): HTMLElement | null {
  const id = wikiAnchorId(target);
  return id ? findElementById(root, id) : null;
}

export function scrollToWikiAnchor(root: ParentNode, target: string): boolean {
  const anchor = findWikiAnchor(root, target);
  if (!anchor) return false;
  const reduceMotion = typeof globalThis.matchMedia === 'function'
    && globalThis.matchMedia('(prefers-reduced-motion: reduce)').matches;
  anchor.scrollIntoView({ behavior: reduceMotion ? 'auto' : 'smooth', block: 'start' });
  return true;
}

function linkKind(target: string): WikiLinkedElement['kind'] {
  if (target.startsWith('#')) return 'anchor';
  if (/^[a-z][a-z0-9+.-]*:/i.test(target)) return 'external';
  return 'doc';
}

function taskReferenceFrom(label: string, target: string): string | null {
  const taskScheme = target.match(/^(?:#?task:)([^/?#]+)/i)?.[1];
  if (taskScheme) return taskScheme;
  return `${label} ${target}`.match(/\b(?:AGT|QS)-[A-Z0-9-]+\b/i)?.[0] ?? null;
}

function parentDir(rel: string): string {
  const index = rel.lastIndexOf('/');
  return index >= 0 ? rel.slice(0, index) : '';
}

function findElementById(root: ParentNode, id: string): HTMLElement | null {
  const direct = Array.from(root.querySelectorAll<HTMLElement>('[id]'))
    .find(element => element.id === id);
  if (direct) return direct;
  for (const element of Array.from(root.querySelectorAll<HTMLElement>('*'))) {
    if (!element.shadowRoot) continue;
    const nested = findElementById(element.shadowRoot, id);
    if (nested) return nested;
  }
  return null;
}

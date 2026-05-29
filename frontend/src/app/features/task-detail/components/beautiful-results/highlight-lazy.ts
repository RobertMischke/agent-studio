/**
 * Lazy-loaded highlight.js wrapper. The core + ~10 commonly used languages
 * are imported on first call from a dynamic import boundary so they don't
 * weigh on the initial bundle. A small alias map normalizes fence labels
 * agents tend to use ("ts" -> "typescript", "sh" -> "bash", etc).
 *
 * Returns highlighted HTML for a single fenced block as a string. Falls
 * back to the original (already HTML-escaped) text when the language is
 * unknown or highlighting throws.
 */
import type { HLJSApi } from 'highlight.js';

let hljsPromise: Promise<HLJSApi> | null = null;

const ALIASES: Record<string, string> = {
  ts: 'typescript',
  tsx: 'typescript',
  js: 'javascript',
  jsx: 'javascript',
  sh: 'bash',
  shell: 'bash',
  zsh: 'bash',
  yml: 'yaml',
  'c#': 'csharp',
  cs: 'csharp',
  md: 'markdown',
  py: 'python'
};

function normalizeLang(raw: string | null | undefined): string | null {
  if (!raw) return null;
  const cleaned = raw.toLowerCase().trim().replace(/^lang-/, '');
  if (!cleaned) return null;
  return ALIASES[cleaned] ?? cleaned;
}

async function loadHljs(): Promise<HLJSApi> {
  if (!hljsPromise) {
    hljsPromise = (async () => {
      const core = (await import('highlight.js/lib/core')).default;
      const langs = await Promise.all([
        import('highlight.js/lib/languages/typescript'),
        import('highlight.js/lib/languages/javascript'),
        import('highlight.js/lib/languages/json'),
        import('highlight.js/lib/languages/bash'),
        import('highlight.js/lib/languages/diff'),
        import('highlight.js/lib/languages/csharp'),
        import('highlight.js/lib/languages/xml'),
        import('highlight.js/lib/languages/scss'),
        import('highlight.js/lib/languages/markdown'),
        import('highlight.js/lib/languages/python'),
        import('highlight.js/lib/languages/yaml'),
        import('highlight.js/lib/languages/plaintext')
      ]);
      core.registerLanguage('typescript', langs[0].default);
      core.registerLanguage('javascript', langs[1].default);
      core.registerLanguage('json', langs[2].default);
      core.registerLanguage('bash', langs[3].default);
      core.registerLanguage('diff', langs[4].default);
      core.registerLanguage('csharp', langs[5].default);
      // Use 'xml' grammar for both html and xml fence labels.
      core.registerLanguage('xml', langs[6].default);
      core.registerLanguage('html', langs[6].default);
      core.registerLanguage('scss', langs[7].default);
      core.registerLanguage('css', langs[7].default);
      core.registerLanguage('markdown', langs[8].default);
      core.registerLanguage('python', langs[9].default);
      core.registerLanguage('yaml', langs[10].default);
      core.registerLanguage('plaintext', langs[11].default);
      return core;
    })();
  }
  return hljsPromise;
}

export async function highlightBlock(
  source: string,
  lang: string | null | undefined
): Promise<{ html: string; language: string | null }> {
  const language = normalizeLang(lang);
  if (!language) return { html: escapeHtml(source), language: null };
  try {
    const hljs = await loadHljs();
    if (!hljs.getLanguage(language)) {
      return { html: escapeHtml(source), language };
    }
    const result = hljs.highlight(source, { language, ignoreIllegals: true });
    return { html: result.value, language };
  } catch {
    return { html: escapeHtml(source), language };
  }
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

/** Reset state for unit tests. */
export function _resetHighlightForTests(): void {
  hljsPromise = null;
}

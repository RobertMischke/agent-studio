import { describe, expect, it } from 'vitest';
import { derivePageType, pageContextKey, pageExcerpt, pageTypeIcon, pageTypeLabel } from './page-context.model';

describe('page context model', () => {
  it('derives the five canonical kinds from meta, registration, and paths', () => {
    expect(derivePageType('guide.md')).toBe('doc');
    expect(derivePageType('concepts/interaction.md', {
      status: null, supersededBy: null, type: 'konzept', analyzedAt: null,
    })).toBe('concept');
    expect(derivePageType('quality/action-bar/index.html', null, new Set([
      'docs/quality/action-bar/index.html',
    ]))).toBe('workbench');
    expect(derivePageType('operations/incidents/history.md')).toBe('incident');
    expect(derivePageType('reports/quality.md')).toBe('report');
  });

  it('uses the Dossier label and the same eye icon for registered pages', () => {
    expect(pageTypeLabel('workbench')).toBe('Dossier');
    expect(pageTypeIcon('workbench')).toBe('eye');
  });

  it('builds a canonical reference and bounded plain-text excerpt', () => {
    const context = {
      projectName: 'PROJ-002',
      relPath: 'concepts/page.md',
      title: 'Page',
      pageType: 'concept' as const,
      excerpt: 'Example',
    };
    expect(pageContextKey(context)).toBe('page:PROJ-002/concepts/page.md');
    expect(pageExcerpt('<h1>Title</h1><script>bad()</script> **Body**', 'fallback'))
      .toBe('Title Body');
  });
});

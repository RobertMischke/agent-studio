import { describe, expect, it } from 'vitest';
import {
  markdownToHtml,
  protectTechnicalMarkdown,
  type MarkdownTaskReference,
} from 'coding-agent-chat/markdown';

const TASK_REFERENCES: readonly MarkdownTaskReference[] = [
  { label: 'AGT-2355', taskKey: 'project::agt-2355' },
];

describe('coding-agent-chat technical markdown protection', () => {
  it('renders an unfenced unified diff as one preformatted diff block', () => {
    const source = [
      'The card AGT-2355 remains visible in prose.',
      '',
      'diff --git a/docs/start/README.md b/docs/start/README.md',
      'index 1111111..2222222 100644',
      '--- a/docs/start/README.md',
      '+++ b/docs/start/README.md',
      '@@ -1,2 +1,2 @@',
      '-| Deck | AGT-2355 |',
      '+| Project facets | AGT-2355 |',
      '+.card {',
      '+  display: grid;',
      '+}',
      '+<header class="page">',
      '+  <svg viewBox="0 0 24 24"></svg>',
      '+</header>',
    ].join('\n');

    const html = markdownToHtml(source, { taskReferences: TASK_REFERENCES });

    expect(html).toContain('<pre class="md-code md-code--lang-diff');
    expect(html).toContain('data-lang="diff"');
    expect(html).not.toContain('<table');
    expect(html).not.toContain('<ul>');
    expect(html.match(/data-task-ref="true"/g)).toHaveLength(1);
    expect(html).toContain('+&lt;header');
    expect(html).toContain('+  &lt;svg');
  });

  it('escapes standalone raw HTML and SVG inside code blocks', () => {
    const source = [
      '<header class="page">',
      '  <h1>Title</h1>',
      '</header>',
      '',
      '<svg viewBox="0 0 24 24">',
      '  <path d="M1 1h2"/>',
      '</svg>',
    ].join('\n');

    const html = markdownToHtml(source);

    expect(html.match(/<pre class="md-code md-code--lang-html/g)).toHaveLength(2);
    expect(html).not.toContain('<header class="page">');
    expect(html).not.toContain('<svg viewBox="0 0 24 24">');
    expect(html).toContain('hljs-name">header');
    expect(html).toContain('hljs-name">svg');
  });

  it.each([
    [
      'unified file headers',
      ['--- a/readme.md', '+++ b/readme.md', '@@ -1 +1 @@', '-old', '+new'],
    ],
    [
      'a hunk header with change evidence',
      ['@@ -1 +1 @@', '-old', '+new'],
    ],
  ])('recognizes an unfenced diff starting with %s', (_label, lines) => {
    const html = markdownToHtml(lines.join('\n'));

    expect(html).toContain('data-lang="diff"');
    expect(html).toContain('-old');
    expect(html).toContain('+new');
  });

  it('leaves authored fences unchanged', () => {
    const source = [
      'Before.',
      '',
      '```diff',
      'diff --git a/a b/a',
      '--- a/a',
      '+++ b/a',
      '@@ -1 +1 @@',
      '-old',
      '+new',
      '```',
      '',
      'After.',
    ].join('\n');

    expect(protectTechnicalMarkdown(source)).toBe(source);
  });

  it('keeps normal prose, task references, lists, and tables in the GFM pipeline', () => {
    const source = [
      'AGT-2355 compares pros + cons.',
      '',
      '- first item',
      '- second item',
      '',
      '| Choice | State |',
      '| --- | --- |',
      '| A | Ready |',
    ].join('\n');

    const protectedSource = protectTechnicalMarkdown(source);
    const html = markdownToHtml(source, { taskReferences: TASK_REFERENCES });

    expect(protectedSource).toBe(source);
    expect(html).toContain('data-task-ref="true"');
    expect(html).toContain('<ul>');
    expect(html).toContain('<table>');
    expect(html).not.toContain('<pre');
  });
});

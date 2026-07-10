import fs from 'node:fs';
import path from 'node:path';
import { test, expect } from '@playwright/test';

const APP_STYLES_ROOT = path.resolve(__dirname, '../../src/app');
const MARKDOWN_STYLES = path.resolve(__dirname, '../../src/styles/_markdown-body.scss');
const DEV_MODE_SOURCE = path.resolve(__dirname, '../../src/dev-mode.ts');

const STRUCTURAL_LEFT_EDGES = new Map<string, RegExp[]>([
  [
    'features/plan-strip/plan-strip.component.scss',
    [/border-left:\s*2px\s+dashed\s+var\(--studio-border-strong\)/],
  ],
  [
    'features/task-detail/components/protocol-pane/run-timeline/run-timeline.component.scss',
    [/border-left:\s*2px\s+solid\s+var\(--studio-border\)/],
  ],
]);

function scssFiles(root: string): string[] {
  return fs.readdirSync(root, { withFileTypes: true }).flatMap((entry) => {
    const fullPath = path.join(root, entry.name);
    if (entry.isDirectory()) return scssFiles(fullPath);
    return entry.isFile() && entry.name.endsWith('.scss') ? [fullPath] : [];
  });
}

function relativeStylePath(file: string): string {
  if (file === MARKDOWN_STYLES) return 'styles/_markdown-body.scss';
  return path.relative(APP_STYLES_ROOT, file).replaceAll('\\', '/');
}

test.describe('style-guide hard rules', () => {
  test('R1: shipping cards, panels, rows, banners, and callouts have no left accent rail', () => {
    const violations: string[] = [];
    const files = [...scssFiles(APP_STYLES_ROOT), MARKDOWN_STYLES];

    for (const file of files) {
      const relative = relativeStylePath(file);
      const structural = STRUCTURAL_LEFT_EDGES.get(relative) ?? [];
      const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);

      lines.forEach((line, index) => {
        const hasSemanticOverride = /border-(?:left|inline-start)-(?:color|width)\s*:\s*(?!0\b)/.test(line);
        const hasWideLeftBorder = /border-(?:left|inline-start)\s*:\s*(?:[2-9]|[1-9]\d)(?:\.\d+)?px\b/.test(line);
        const hasInsetLeftRail = /box-shadow\s*:\s*inset\s+\d+(?:\.\d+)?(?:px|rem|em)\s+0(?:\s|;)/.test(line)
          && !line.includes('--studio-nav-active-bar');
        const isStructural = structural.some((pattern) => pattern.test(line));

        if ((hasSemanticOverride || hasWideLeftBorder || hasInsetLeftRail) && !isStructural) {
          violations.push(`${relative}:${index + 1}: ${line.trim()}`);
        }
      });
    }

    expect(violations, violations.join('\n')).toEqual([]);
  });

  test('R1: the DEV marker uses its badge without a full-height left-edge stripe', () => {
    const source = fs.readFileSync(DEV_MODE_SOURCE, 'utf8');
    expect(source).not.toMatch(/body::before\s*\{/);
  });
});

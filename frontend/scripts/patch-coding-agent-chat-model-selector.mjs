import { readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';

/**
 * Compatibility bridge for coding-agent-chat 0.3.2.
 *
 * Studio consumes the published package, while the selector source is owned by
 * that package rather than this host. Keep the canonical component in place and
 * patch its published partial-Ivy output until the next package release carries
 * disabled CLI reasons, the balanced/faded picker layout, and the paste-only
 * attachment composer. The exact-version and occurrence guards make a package
 * upgrade fail loudly instead of applying stale text rewrites.
 */
const require = createRequire(import.meta.url);
const packageJsonPath = require.resolve('coding-agent-chat/package.json');
const packageRoot = dirname(packageJsonPath);
const packageJson = JSON.parse(readFileSync(packageJsonPath, 'utf8'));

if (packageJson.version !== '0.3.2') {
  throw new Error(
    `The coding-agent-chat compatibility patch expects 0.3.2, found ${packageJson.version}.`,
  );
}

function replaceExact(source, before, after, expectedCount, label) {
  const actualCount = source.split(before).length - 1;
  if (actualCount === 0 && source.includes(after)) return source;
  if (actualCount !== expectedCount) {
    throw new Error(
      `Could not apply ${label}: expected ${expectedCount} source occurrence(s), found ${actualCount}.`,
    );
  }
  return source.replaceAll(before, after);
}

function removeExact(source, target, expectedCount, label) {
  const actualCount = source.split(target).length - 1;
  if (actualCount === 0) return source;
  if (actualCount !== expectedCount) {
    throw new Error(
      `Could not apply ${label}: expected ${expectedCount} source occurrence(s), found ${actualCount}.`,
    );
  }
  return source.replaceAll(target, '');
}

const composerPath = join(packageRoot, 'fesm2022', 'coding-agent-chat-composer.mjs');
let composer = readFileSync(composerPath, 'utf8');

composer = replaceExact(
  composer,
  `    onCliPillClick(id) {
        if (id !== this.draftCliType()) {`,
  `    onCliPillClick(id) {
        if (this.cliDisabledReason(id))
            return;
        if (id !== this.draftCliType()) {`,
  1,
  'disabled CLI click guard',
);

composer = replaceExact(
  composer,
  `    onCliPillKeydown(current, event) {
        const ids = this.cliOptions().map((o) => o.id);`,
  `    onCliPillKeydown(current, event) {
        const ids = this.cliOptions()
            .filter((option) => !option.disabledReason)
            .map((option) => option.id);`,
  1,
  'disabled CLI keyboard guard',
);

composer = replaceExact(
  composer,
  `    cliOptionIcon(id) {
        return this.cliOptions().find((o) => o.id === id)?.icon ?? '·';
    }`,
  `    cliOptionIcon(id) {
        return this.cliOptions().find((o) => o.id === id)?.icon ?? '·';
    }
    cliDisabledReason(id) {
        return this.cliOptions().find((option) => option.id === id)?.disabledReason ?? null;
    }`,
  1,
  'disabled CLI reason accessor',
);

composer = replaceExact(
  composer,
  `        <div class=\\"model-picker__pills\\"\\n             role=\\"radiogroup\\"\\n             aria-label=\\"CLI\\"`,
  `        <div class=\\"model-picker__pills model-picker__pills--cli\\"\\n             role=\\"radiogroup\\"\\n             aria-label=\\"CLI\\"`,
  2,
  'CLI option grid class',
);

composer = replaceExact(
  composer,
  `                    class=\\"model-picker__pill\\"\\n                    [class.model-picker__pill--active]=\\"draftCliType() === opt.id\\"\\n                    role=\\"radio\\"\\n                    [attr.aria-checked]=\\"draftCliType() === opt.id\\"\\n                    [attr.data-testid]=\\"pickerTestidPrefix() + '-cli-' + opt.id\\"`,
  `                    class=\\"model-picker__pill model-picker__pill--cli\\"\\n                    [class.model-picker__pill--active]=\\"draftCliType() === opt.id\\"\\n                    role=\\"radio\\"\\n                    [attr.aria-checked]=\\"draftCliType() === opt.id\\"\\n                    [attr.data-testid]=\\"pickerTestidPrefix() + '-cli-' + opt.id\\"\\n                    [disabled]=\\"!!opt.disabledReason\\"`,
  2,
  'disabled CLI button binding',
);

composer = replaceExact(
  composer,
  `              <span class=\\"model-picker__pill-label\\">{{ opt.label }}</span>\\n            </button>`,
  `              <span class=\\"model-picker__pill-label\\">{{ opt.label }}</span>\\n              @if (opt.disabledReason) {\\n                <span class=\\"model-picker__pill-reason\\">{{ opt.disabledReason }}</span>\\n              }\\n            </button>`,
  2,
  'disabled CLI visible reason',
);

composer = replaceExact(
  composer,
  `        <div class=\\"model-picker__pills\\"\\n             role=\\"radiogroup\\"\\n             aria-label=\\"Thinking level\\"`,
  `        <div class=\\"model-picker__pills model-picker__pills--levels\\"\\n             role=\\"radiogroup\\"\\n             aria-label=\\"Thinking level\\"`,
  2,
  'balanced thinking-level grid class',
);

composer = replaceExact(
  composer,
  `.model-picker{position:fixed;z-index:40;min-width:300px;max-width:380px;display:flex;flex-direction:column;gap:12px;padding:12px;`,
  `.model-picker{position:fixed;z-index:40;width:min(380px,calc(100vw - var(--studio-spacing-4, 16px) * 2));min-width:min(320px,calc(100vw - var(--studio-spacing-4, 16px) * 2));max-width:380px;display:flex;flex-direction:column;gap:var(--studio-spacing-3, 12px);padding:var(--studio-spacing-3, 12px);`,
  2,
  'popover width and spacing tokens',
);

composer = replaceExact(
  composer,
  `.model-picker__section{display:flex;flex-direction:column;gap:6px}`,
  `.model-picker__section{display:flex;flex-direction:column;gap:var(--studio-spacing-2, 8px)}`,
  2,
  'section spacing token',
);

composer = replaceExact(
  composer,
  `.model-picker__pills{display:flex;flex-wrap:wrap;gap:6px}`,
  `.model-picker__pills{display:flex;flex-wrap:wrap;gap:var(--studio-spacing-2, 8px)}.model-picker__pills--cli{display:grid;grid-template-columns:repeat(3,minmax(0,1fr))}.model-picker__pills--levels{display:grid;grid-template-columns:repeat(3,minmax(0,1fr))}.model-picker__pills--levels .model-picker__pill{justify-content:center}`,
  2,
  'balanced option grids',
);

composer = replaceExact(
  composer,
  `.model-picker__pills--column{flex-direction:column;flex-wrap:nowrap;align-items:stretch;max-height:220px;overflow-y:auto}`,
  `.model-picker__pills--column{flex-direction:column;flex-wrap:nowrap;align-items:stretch;max-height:min(236px,34vh);overflow-y:auto;overscroll-behavior:contain;scrollbar-gutter:stable;padding-block-end:var(--studio-spacing-3, 12px);mask-image:linear-gradient(to bottom,currentColor 0,currentColor calc(100% - var(--studio-spacing-3, 12px)),transparent 100%);scrollbar-color:var(--studio-border-strong,var(--studio-border)) transparent;scrollbar-width:thin}`,
  2,
  'model list scroll and fade',
);

composer = replaceExact(
  composer,
  `.model-picker__pill:hover{background:`,
  `.model-picker__pill:hover:not(:disabled){background:`,
  2,
  'disabled option hover guard',
);

composer = replaceExact(
  composer,
  `.model-picker__pill--row{border-radius:7px;justify-content:flex-start}`,
  `.model-picker__pill--row{border-radius:7px;justify-content:flex-start}.model-picker__pill--cli{min-width:0;min-height:48px;flex-direction:column;align-items:flex-start;justify-content:center;gap:var(--studio-spacing-1, 4px);border-radius:7px}.model-picker__pill:disabled{cursor:not-allowed;color:var(--studio-fg-muted);background:var(--studio-surface-muted,transparent)}.model-picker__pill:disabled .model-picker__pill-icon,.model-picker__pill:disabled .model-picker__pill-label{opacity:.72}.model-picker__pill-reason{font-size:10px;line-height:1.25;color:var(--studio-fg-dim);font-weight:400}`,
  2,
  'disabled CLI option layout',
);

composer = removeExact(
  composer,
  `    fileInputRef = viewChild('fileInput', ...(ngDevMode ? [{ debugName: "fileInputRef" }] : /* istanbul ignore next */ []));
`,
  1,
  'composer file input query',
);

composer = removeExact(
  composer,
  `    triggerFilePicker() {
        this.fileInputRef()?.nativeElement.click();
    }
    onFileInputChange(event) {
        const target = event.target;
        const files = Array.from(target.files ?? []);
        for (const file of files) {
            if (file.type.startsWith('image/'))
                this.addAttachment(file);
        }
        target.value = '';
    }
`,
  1,
  'composer file picker handlers',
);

composer = removeExact(
  composer,
  `        @if (allowAttachments()) {\\n          <button\\n            type=\\"button\\"\\n            class=\\"chat__icon-btn\\"\\n            [cacTooltip]=\\"'Attach image'\\"\\n            data-testid=\\"chat-attach\\"\\n            [disabled]=\\"disabled()\\"\\n            (click)=\\"triggerFilePicker()\\"\\n          >\\n            \\uD83D\\uDCCE\\n          </button>\\n          <input\\n            #fileInput\\n            type=\\"file\\"\\n            accept=\\"image/*\\"\\n            multiple\\n            class=\\"chat__file-input\\"\\n            (change)=\\"onFileInputChange($event)\\"\\n          />\\n        }\\n`,
  2,
  'composer file picker template',
);

composer = removeExact(
  composer,
  `.chat__icon-btn{background:var(--studio-bg-hover, rgba(255, 255, 255, .06));border:1px solid var(--studio-border, rgba(255, 255, 255, .12));color:var(--studio-fg-dim, #cbd5e1);width:36px;height:36px;border-radius:8px;cursor:pointer;font-size:16px;line-height:1}.chat__icon-btn:hover:not(:disabled){background:var(--studio-bg-elevated, rgba(99, 102, 241, .18));color:var(--studio-fg, #ddd6fe);border-color:var(--studio-accent, rgba(167, 139, 250, .55))}.chat__icon-btn:disabled{opacity:.4;cursor:not-allowed}.chat__file-input{display:none}`,
  2,
  'composer file picker styles',
);

composer = removeExact(
  composer,
  `, { propertyName: "fileInputRef", first: true, predicate: ["fileInput"], descendants: true, isSignal: true }`,
  1,
  'composer file input query metadata',
);

composer = removeExact(
  composer,
  `, fileInputRef: [{ type: i0.ViewChild, args: ['fileInput', { isSignal: true }] }]`,
  1,
  'composer file input decorator metadata',
);

writeFileSync(composerPath, composer);

const coreTypesPath = join(packageRoot, 'types', 'coding-agent-chat-core.d.ts');
let coreTypes = readFileSync(coreTypesPath, 'utf8');
coreTypes = replaceExact(
  coreTypes,
  `    /** Single glyph / emoji rendered before the label. */
    icon?: string;
}`,
  `    /** Single glyph / emoji rendered before the label. */
    icon?: string;
    /** Visible reason when this CLI is listed but unavailable in the current host policy. */
    disabledReason?: string | null;
}`,
  1,
  'ChatCliOption disabled reason type',
);
writeFileSync(coreTypesPath, coreTypes);

const composerTypesPath = join(packageRoot, 'types', 'coding-agent-chat-composer.d.ts');
let composerTypes = readFileSync(composerTypesPath, 'utf8');
composerTypes = removeExact(
  composerTypes,
  `    private readonly fileInputRef;
`,
  1,
  'composer file input type declaration',
);
composerTypes = removeExact(
  composerTypes,
  `    triggerFilePicker(): void;
    onFileInputChange(event: Event): void;
`,
  1,
  'composer file picker handler declarations',
);
composerTypes = replaceExact(
  composerTypes,
  `    cliOptionIcon(id: string): string;
    private applyCatalog;`,
  `    cliOptionIcon(id: string): string;
    cliDisabledReason(id: string): string | null;
    private applyCatalog;`,
  1,
  'ModelSelectorComponent disabled reason declaration',
);
writeFileSync(composerTypesPath, composerTypes);

console.log('Applied the coding-agent-chat 0.3.2 compatibility patch.');

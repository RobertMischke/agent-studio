// @ts-check
const eslint = require('@eslint/js');
const tseslint = require('typescript-eslint');
const angular = require('angular-eslint');

module.exports = tseslint.config(
  {
    files: ['**/*.ts'],
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...tseslint.configs.stylistic,
      ...angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      '@angular-eslint/directive-selector': [
        'error',
        { type: 'attribute', prefix: 'app', style: 'camelCase' },
      ],
      '@angular-eslint/component-selector': [
        'error',
        { type: 'element', prefix: ['app', 'mockup'], style: 'kebab-case' },
      ],
      // ADR-0034 / Cycle 9h+10j: cross-feature imports must use the
      // feature barrel (./features/<name>), not deep paths. The barrel
      // is each feature's public API; deep imports turn every internal
      // file into an external contract.
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: [
                '**/features/*/components/**',
                '**/features/*/services/**',
                '**/features/*/models/**',
                '**/features/*/state/**',
              ],
              message:
                'Cross-feature imports must use the feature barrel (./features/<name>), not deep paths. See frontend/AGENTS.md.',
            },
          ],
        },
      ],
    },
  },
  {
    files: ['**/*.html'],
    extends: [
      ...angular.configs.templateRecommended,
      ...angular.configs.templateAccessibility,
    ],
    rules: {},
  },
  {
    // The barrel-only rule is a guard for cross-feature wiring.
    // Files inside a feature freely use relative paths into their own
    // components/services/models/state — that's not a deep import.
    files: ['**/features/*/**/*.ts'],
    rules: {
      'no-restricted-imports': 'off',
    },
  },
  {
    // Spec + e2e fixture files often need to reach into private
    // implementation details. Don't punish them.
    files: ['**/*.spec.ts', '**/e2e/**/*.ts'],
    rules: {
      'no-restricted-imports': 'off',
    },
  },
);

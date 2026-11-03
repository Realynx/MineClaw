import nextPlugin from '@next/eslint-plugin-next';
import { defineConfig } from 'eslint/config';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import base from '../../eslint.base.js';

export default defineConfig(
  base,
  {
    ignores: ['public/**', '.next-mock/**', '.next-e2e/**', 'next-env.d.ts', 'test-results/**', 'playwright-report/**'],
  },
  {
    files: ['src/**/*.{ts,tsx}', 'test/**/*.{ts,tsx}', 'e2e/**/*.ts'],
    languageOptions: { globals: { ...globals.browser } },
    plugins: { 'react-hooks': reactHooks, '@next/next': nextPlugin },
    rules: {
      ...reactHooks.configs.recommended.rules,
      ...nextPlugin.configs.recommended.rules,
      ...nextPlugin.configs['core-web-vitals'].rules,
      '@typescript-eslint/no-misused-promises': ['error', { checksVoidReturn: { attributes: false } }],
      // Resetting local state when a prop/key changes is deliberate here (drawers, dialogs, reveal-on-select).
      'react-hooks/set-state-in-effect': 'off',
      // TanStack Virtual is known not to be compiler-memoisable; we do not use the React Compiler.
      'react-hooks/incompatible-library': 'off',
    },
  },
  {
    files: ['scripts/**/*.mjs'],
    // screenshots.mjs runs some functions inside the page (page.evaluate)
    languageOptions: { globals: { ...globals.node, ...globals.browser } },
    rules: { 'no-console': 'off' },
  },
);

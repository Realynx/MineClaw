import { defineConfig } from 'eslint/config';
import base from '../../eslint.base.js';

export default defineConfig(base, {
  files: ['src/**/*.ts'],
  rules: {
    // The engine is plain data in and out so it runs the same on the page, in a worker and in tests. The workspace hoists
    // every package, so an undeclared import (three, react, a node built-in) would resolve here; this is what stops it.
    'no-restricted-imports': [
      'error',
      {
        patterns: [
          {
            regex: '^[^.]',
            message: 'src/ imports only its own files (relative paths): no packages, no node built-ins.',
          },
        ],
      },
    ],
  },
});

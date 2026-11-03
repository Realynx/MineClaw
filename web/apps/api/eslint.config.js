import { defineConfig } from 'eslint/config';
import base from '../../eslint.base.js';

export default defineConfig(base, {
  // supertest's `res.body` is `any` by design; tests assert on it directly.
  files: ['test/**/*.ts'],
  rules: {
    '@typescript-eslint/no-unsafe-member-access': 'off',
    '@typescript-eslint/no-unsafe-assignment': 'off',
    '@typescript-eslint/no-unsafe-argument': 'off',
    '@typescript-eslint/no-unsafe-call': 'off',
  },
});

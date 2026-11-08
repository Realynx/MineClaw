import { expect, it } from 'vitest';
import { StoryDirectiveRequest } from '../src/story.js';
const request = (directive: unknown) => StoryDirectiveRequest.safeParse({ bots: ['main'], directive, requestId: 'controller-request-1' }).success;
it('accepts full controller instructions but keeps ordinary map directive limits and forbids spoofed identities', () => {
  expect(request({ kind: 'text', colony: 'story-1', text: 'x'.repeat(4000) })).toBe(true);
  expect(request({ kind: 'text', colony: 'story-1', text: 'x'.repeat(4001) })).toBe(false);
  expect(request({ kind: 'text', text: 'x'.repeat(201) })).toBe(false);
  expect(request({ kind: 'mine', colony: 'story-1', text: 'do it' })).toBe(false);
  expect(request({ kind: 'text', colony: 'story-1', player: 'someone', text: 'do it' })).toBe(false);
  expect(request({ kind: 'text', colony: 'story-1' })).toBe(false);
});

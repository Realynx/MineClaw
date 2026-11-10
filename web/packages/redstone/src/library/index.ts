/**
 * The built-in components. Each is listed for a game version only when test/library proves its spec there, in every
 * rotation and mirror and with every feeder: nothing unproven is listed.
 */
import { ARITHMETIC } from './arithmetic.js';
import { LOGIC } from './logic.js';
import { MEMORY } from './memory.js';
import { MODERN } from './modern.js';
import { TIMING } from './timing.js';
import type { ComponentDef } from './types.js';

export const LIBRARY: readonly ComponentDef[] = [...LOGIC, ...MEMORY, ...TIMING, ...ARITHMETIC, ...MODERN];

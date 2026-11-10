/**
 * Every kind of part the engine simulates. A block with redstone behaviour that has no handler here is reported as
 * `unsupported` and stays as placed.
 */
import { wire } from '../wire.js';
import { button } from './button.js';
import { comparator } from './comparator.js';
import { dispenser } from './dispenser.js';
import { door, fenceGate, trapdoor } from './door.js';
import type { ComponentHandler } from './handler.js';
import { lamp } from './lamp.js';
import { lever } from './lever.js';
import {
  container,
  daylightSensor,
  detectorRail,
  hopper,
  noteBlock,
  redstoneBlock,
  tnt,
  trappedChest,
} from './misc.js';
import { observer } from './observer.js';
import { movingBlock, piston, pistonHead } from './piston.js';
import { plate } from './plate.js';
import { repeater } from './repeater.js';
import { torch } from './torch.js';

export const HANDLERS: readonly ComponentHandler[] = [
  wire,
  torch,
  repeater,
  lever,
  button,
  plate,
  comparator,
  piston,
  pistonHead,
  movingBlock,
  lamp,
  door,
  trapdoor,
  fenceGate,
  dispenser,
  observer,
  noteBlock,
  tnt,
  hopper,
  container,
  redstoneBlock,
  daylightSensor,
  trappedChest,
  detectorRail,
];

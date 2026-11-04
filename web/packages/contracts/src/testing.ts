/** Test and mock helpers (not for production code): the synthetic Story Mode director the mocks run. */
export { StorySim, isLog, type SimEntity, type StoryWorld, type StorySimOptions } from './storySim.js';
export { SIM_MATERIALS, DEFAULT_POLICIES, gearOp, gearOrder, policyKinds } from './storySimGear.js';
export {
  CLOUD_MOCK_ARRANGEMENTS,
  CLOUD_MOCK_FIDELITY,
  cloudMockDistance,
  memoryCloudMock,
  type CloudMockNode,
  type CloudMockOptions,
} from './cloudMock.js';
export { mockInventory } from './mockInventory.js';
export { mockContainer, mockContainers, MOCK_CONTAINERS } from './mockContainers.js';

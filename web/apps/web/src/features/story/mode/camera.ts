import { canCaptureController, requestControllerFullscreen } from '../controllerInput';
import type { Story } from './commands';

/** Request fullscreen in the trusted user gesture; the map waits for it before capturing the mouse. */
export function toggleControllerFly(c: Story): void {
  if (c.view !== 'fly' && c.fullscreen.el && canCaptureController())
    void requestControllerFullscreen(c.fullscreen.el);
  c.setView((view) => view === 'fly' ? 'top' : 'fly');
}

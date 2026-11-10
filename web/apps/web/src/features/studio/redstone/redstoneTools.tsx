/** The tool a redstone design has on its rail besides the build tools. */
import { SlidersHorizontal } from 'lucide-react';
import type { ToolDef } from '../StudioChrome';

export const CONFIGURE_TOOL: ToolDef = {
  id: 'configure',
  label: 'Configure (right-click: a repeater’s delay, a comparator’s mode, a lever on or off; left-click inspects)',
  key: 'C',
  icon: <SlidersHorizontal className="size-4" />,
};

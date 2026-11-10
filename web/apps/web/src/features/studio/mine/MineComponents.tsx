'use client';

import { CopyPlus, Eye, EyeOff, Trash2 } from 'lucide-react';
import { ComponentOutliner, OutlinerRowAction, type OutlinerItem } from '../components/ComponentOutliner';
import { ModuleGlyph, moduleLabel, type ModulePaletteProps } from './ModulePalette';

/** A persistent outliner: component selection is independent of terrain visibility and edit permissions. */
export function MineComponents(
  p: Pick<
    ModulePaletteProps,
    | 'modules'
    | 'selectedModule'
    | 'onSelectModule'
    | 'onFrameModule'
    | 'onCopyModule'
    | 'onPasteModule'
    | 'onDuplicateModule'
    | 'onRemoveModule'
    | 'onCombineModules'
    | 'checkedModules'
    | 'onCheckModules'
    | 'canPasteModule'
    | 'onVisibilityModule'
  > & { disabled?: boolean },
) {
  const modules = p.modules ?? [];
  const items: OutlinerItem[] = modules.map((item) => ({
    id: item.id,
    name: moduleLabel(item.spec),
    detail: `${item.at[0]}, ${item.at[1]}, ${item.at[2]}`,
    title: `${moduleLabel(item.spec)} · ${item.at.join(', ')}`,
    glyph: <ModuleGlyph kind={item.spec.kind} compact />,
    dim: item.visible === false,
  }));
  return (
    <ComponentOutliner
      items={items}
      selectedId={p.selectedModule}
      disabled={p.disabled}
      onSelect={p.onSelectModule}
      onFrame={p.onFrameModule}
      onCopy={p.onCopyModule}
      onPaste={p.onPasteModule}
      onDuplicate={p.onDuplicateModule}
      onCombine={p.onCombineModules}
      canPaste={p.canPasteModule}
      checked={p.checkedModules}
      onCheck={p.onCheckModules}
      labels={{ list: 'Mine components', combine: 'Combine mine components', testId: 'mine-components' }}
      hint="Select here or in the mine. Double-click to frame. Ctrl- or Shift-click ticks pieces to combine."
      empty={
        <>
          <p>Add a layout below to create a movable component.</p>
          <p className="mt-1">Manual digs and older presets remain editable cells, without component groups.</p>
        </>
      }
      combineHint="Choose pieces to move, rotate and copy as one custom component (Ctrl+G)."
      rowActions={(item, label) => (
        <>
          <OutlinerRowAction
            label={`${item.dim ? 'Show' : 'Hide'} ${label}`}
            pressed={!item.dim}
            title="Show or hide this component's wireframe. The saved excavation and mining tasks stay unchanged."
            onClick={() => p.onVisibilityModule?.(item.id, !!item.dim)}
          >
            {item.dim ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
          </OutlinerRowAction>
          <OutlinerRowAction
            label={`Duplicate ${label}`}
            title="Duplicate: place a copy at the same height (Ctrl/Cmd+D)"
            disabled={p.disabled || !p.onDuplicateModule}
            onClick={() => p.onDuplicateModule?.(item.id)}
          >
            <CopyPlus className="size-3.5" />
          </OutlinerRowAction>
          <OutlinerRowAction
            label={`Delete ${label}`}
            title="Delete this component and the excavation only it plans (Ctrl+Z undoes)"
            danger
            disabled={p.disabled || !p.onRemoveModule}
            onClick={() => p.onRemoveModule?.(item.id)}
          >
            <Trash2 className="size-3.5" />
          </OutlinerRowAction>
        </>
      )}
    />
  );
}

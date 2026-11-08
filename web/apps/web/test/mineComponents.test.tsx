import { fireEvent, render, screen, within } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { MinePanel } from '@/features/studio/MinePanel';
import { MineComponents } from '@/features/studio/mine/MineComponents';
import { ModuleSettings } from '@/features/studio/mine/ModulePalette';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';
import { defaultModule, type MineModuleInstance } from '@/features/studio/mine/modules';

const modules: MineModuleInstance[] = [
  { id: 'tunnel', spec: defaultModule('tunnel'), at: [24, 11, 31] },
  { id: 'chamber', spec: defaultModule('chamber'), at: [36, 16, 31] },
  { id: 'spiral', spec: defaultModule('spiral'), at: [36, 22, 36] },
];

function mountPanel(options: { canWrite?: boolean; busy?: boolean; acceptMoves?: boolean } = {}) {
  const onSelect = vi.fn(), onFrame = vi.fn(), onPosition = vi.fn();
  function Panel() {
    const [selected, select] = useState<string | null>(null);
    const [items, setItems] = useState(modules);
    return <MinePanel terrain={MineTerrain.for('12345', '1.12.2')} version="1.12.2" seed="12345" serverSeed={null} preferredFor={null}
      dug={() => []} rev={0} level={11} onLevel={() => undefined} xray onXray={() => undefined}
      at={null} canWrite={options.canWrite ?? true} busy={options.busy ?? false}
      onSeed={() => undefined} onInsert={() => undefined} onPreset={() => undefined}
      modules={items} selectedModule={selected}
      onSelectModule={(id) => { onSelect(id); select(id); }} onFrameModule={onFrame}
      onPositionModule={(id, at) => { onPosition(id, at); if (options.acceptMoves) setItems((old) => old.map((item) => item.id === id ? { ...item, at } : item)); }} />;
  }
  render(<Panel />);
  return { onSelect, onFrame, onPosition };
}

describe('Mine Designer components', () => {
  it('keeps the outliner above the scrolling inspector and palette and selects or frames existing pieces', () => {
    const { onSelect, onFrame } = mountPanel();
    const outliner = screen.getByTestId('mine-components'), properties = screen.getByTestId('mine-properties');
    expect(outliner.parentElement).toBe(properties.parentElement);
    expect(outliner.nextElementSibling).toBe(properties);
    expect(screen.getAllByRole('listbox', { name: 'Mine components' })).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Frame selected component' })).toBeDisabled();
    const chamber = screen.getByRole('option', { name: 'Chamber 2' });
    fireEvent.click(chamber);
    expect(onSelect).toHaveBeenLastCalledWith('chamber');
    expect(chamber).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByLabelText('Position X')).toHaveValue(36);
    expect(screen.getByLabelText('Position Y')).toHaveValue(16);
    expect(properties.firstElementChild).toContainElement(screen.getByRole('region', { name: 'Layout settings' }));
    fireEvent.click(screen.getByRole('button', { name: 'Frame selected component' }));
    expect(onFrame).toHaveBeenLastCalledWith('chamber');
    fireEvent.doubleClick(screen.getByRole('option', { name: 'Tunnel 1' }));
    expect(onFrame).toHaveBeenLastCalledWith('tunnel');
    expect(screen.getByLabelText('Position X')).toHaveValue(24);
    expect(within(properties).getAllByRole('button', { name: /^Place / })).toHaveLength(6);
    expect(screen.getByTestId('ore-ruler')).toBeInTheDocument();
  });

  it('navigates components with arrow keys and Home/End without capturing the viewport', () => {
    const { onFrame } = mountPanel();
    const tunnel = screen.getByRole('option', { name: 'Tunnel 1' });
    tunnel.focus();
    fireEvent.keyDown(tunnel, { key: 'ArrowDown' });
    const chamber = screen.getByRole('option', { name: 'Chamber 2' });
    expect(chamber).toHaveFocus();
    expect(chamber).toHaveAttribute('aria-selected', 'true');
    fireEvent.keyDown(chamber, { key: 'End' });
    expect(screen.getByRole('option', { name: 'Spiral shaft 3' })).toHaveFocus();
    fireEvent.keyDown(document.activeElement!, { key: 'Home' });
    expect(tunnel).toHaveFocus();
    fireEvent.keyDown(tunnel, { key: 'Enter' });
    expect(tunnel).toHaveAttribute('aria-selected', 'true');
    expect(onFrame).not.toHaveBeenCalled();
  });

  it('reveals a newly selected inspector without scrolling again when its position changes', () => {
    mountPanel({ acceptMoves: true });
    const properties = screen.getByTestId('mine-properties');
    const outliner = screen.getByTestId('mine-components');
    properties.scrollTop = 600;
    outliner.scrollTop = 15;
    fireEvent.click(screen.getByRole('option', { name: 'Tunnel 1' }));
    expect(properties.scrollTop).toBe(0);
    expect(outliner.scrollTop).toBe(15);
    properties.scrollTop = 42;
    const x = screen.getByLabelText('Position X');
    fireEvent.change(x, { target: { value: '25' } });
    fireEvent.blur(x);
    expect(x).toHaveValue(25);
    expect(properties.scrollTop).toBe(42);
    fireEvent.click(screen.getByRole('option', { name: 'Chamber 2' }));
    expect(properties.scrollTop).toBe(0);
  });

  it('frames the focused outliner row with F or Numpad Decimal without moving keyboard focus', () => {
    const { onFrame } = mountPanel();
    const chamber = screen.getByRole('option', { name: 'Chamber 2' });
    chamber.focus();
    fireEvent.keyDown(chamber, { key: 'f', code: 'KeyF' });
    expect(onFrame).toHaveBeenCalledExactlyOnceWith('chamber');
    expect(chamber).toHaveAttribute('aria-selected', 'true');
    expect(chamber).toHaveFocus();
    fireEvent.keyDown(chamber, { key: '.', code: 'NumpadDecimal' });
    expect(onFrame).toHaveBeenCalledTimes(2);
    expect(chamber).toHaveFocus();
    fireEvent.keyDown(chamber, { key: 'f', code: 'KeyF', ctrlKey: true });
    expect(onFrame).toHaveBeenCalledTimes(2);
  });

  it('commits position once on blur or Enter, discards Escape, and retains accepted coordinates after a refused move', () => {
    const { onPosition } = mountPanel();
    fireEvent.click(screen.getByRole('option', { name: 'Tunnel 1' }));
    const x = screen.getByLabelText('Position X');
    x.focus();
    fireEvent.change(x, { target: { value: '3' } });
    expect(onPosition).not.toHaveBeenCalled();
    fireEvent.change(x, { target: { value: '32' } });
    fireEvent.keyDown(x, { key: 'Enter' });
    expect(onPosition).toHaveBeenCalledExactlyOnceWith('tunnel', [32, 11, 31]);
    // The parent keeps its accepted position when the controller refuses a move.
    expect(x).toHaveValue(24);
    x.focus();
    fireEvent.change(x, { target: { value: '0' } });
    fireEvent.blur(x);
    expect(onPosition).toHaveBeenLastCalledWith('tunnel', [0, 11, 31]);
    expect(x).toHaveValue(24);
    const y = screen.getByLabelText('Position Y');
    y.focus();
    fireEvent.change(y, { target: { value: '48' } });
    fireEvent.keyDown(y, { key: 'Escape' });
    expect(onPosition).toHaveBeenCalledTimes(2);
    expect(y).toHaveValue(11);
  });

  it.each([{ canWrite: false }, { busy: true }])('allows selection and framing when mutations are unavailable: %j', (options) => {
    const { onFrame } = mountPanel(options);
    fireEvent.click(screen.getByRole('option', { name: 'Chamber 2' }));
    expect(screen.getByLabelText('Position X')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Move' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Remove' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Place Tunnel' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Frame selected component' }));
    expect(onFrame).toHaveBeenCalledWith('chamber');
  });

  it('explains why ungrouped cells are absent rather than inventing components', () => {
    render(<MineComponents modules={[]} />);
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(screen.getByText(/Manual digs and older presets remain editable cells/)).toBeInTheDocument();
  });

  it('keeps pending placement settings independent of the selected component', () => {
    const onUpdatePlacement = vi.fn(), onUpdateModule = vi.fn(), onPositionModule = vi.fn();
    render(<ModuleSettings modules={modules} selectedModule="tunnel" placingModule={defaultModule('chamber')} disabled={false}
      onUpdatePlacement={onUpdatePlacement} onUpdateModule={onUpdateModule} onPositionModule={onPositionModule} />);
    expect(screen.queryByLabelText('Position X')).toBeNull();
    const length = screen.getByLabelText('Room length');
    fireEvent.change(length, { target: { value: '12' } });
    expect(onUpdatePlacement).not.toHaveBeenCalled();
    fireEvent.blur(length);
    expect(onUpdatePlacement).toHaveBeenCalledWith(expect.objectContaining({ kind: 'chamber', length: 12 }));
    expect(onUpdateModule).not.toHaveBeenCalled();
    expect(onPositionModule).not.toHaveBeenCalled();
  });
});

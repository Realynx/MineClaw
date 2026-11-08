import { useState } from 'react';
import { tickClock, type StoryTimeRange } from '@mineclaw/contracts';

/** Slider spans for the saved interval, including a range that wraps through dawn. */
export function timeRangeSpans(range?: StoryTimeRange | null): Array<{ left: number; width: number }> {
  if (!range || range.start === range.end) return [];
  const start = (range.start / 24000) * 100,
    end = (range.end / 24000) * 100;
  return start < end
    ? [{ left: start, width: end - start }]
    : [
        { left: start, width: 100 - start },
        { left: 0, width: end },
      ];
}

/** A saved world policy: editing its endpoints does not jump the clock until Apply. */
export function TimeRangeEditor({
  range,
  disabled,
  onSave,
}: {
  range?: StoryTimeRange | null;
  disabled: boolean;
  onSave: (range: StoryTimeRange | null) => Promise<boolean>;
}) {
  const [enabled, setEnabled] = useState(!!range);
  const [start, setStart] = useState(range?.start ?? 0);
  const [end, setEnd] = useState(range?.end ?? 12000);
  const [saving, setSaving] = useState(false);
  const [open, setOpen] = useState(false);
  const changed = enabled !== !!range || (enabled && (start !== range?.start || end !== range?.end));
  const valid = !enabled || start !== end;
  const save = async () => {
    setSaving(true);
    try {
      await onSave(enabled ? { start, end } : null);
    } finally {
      setSaving(false);
    }
  };
  return (
    <div className="mt-1">
      <button
        type="button"
        aria-label="Time loop options"
        aria-expanded={open}
        className="flex w-full items-center gap-1.5 py-1 text-left text-[10.5px] text-muted hover:text-fg"
        onClick={() => setOpen((value) => !value)}
      >
        <span aria-hidden>{open ? '▾' : '▸'}</span>
        <span>Time loop</span>
        <span className={range ? 'ml-auto text-warn tabular-nums' : 'ml-auto text-subtle'}>
          {range ? `${tickClock(range.start)}–${tickClock(range.end)}` : 'Off'}
          {changed ? ' · unsaved' : ''}
        </span>
      </button>
      {open ? (
        <fieldset className="mt-2 space-y-1.5 border-t border-border pt-2" disabled={disabled || saving}>
          <label className="flex items-center gap-2 text-xs">
            <input
              type="checkbox"
              checked={enabled}
              onChange={(e) => setEnabled(e.target.checked)}
              className="accent-[#f5c542]"
            />
            Loop time range
          </label>
          {enabled ? (
            <>
              {(['Start', 'End'] as const).map((label) => {
                const value = label === 'Start' ? start : end;
                return (
                  <label key={label} className="flex items-center gap-2 text-[11px]">
                    <span className="w-8">{label}</span>
                    <input
                      type="range"
                      min={0}
                      max={23999}
                      step={50}
                      value={value}
                      aria-label={`${label} of time range`}
                      aria-valuetext={tickClock(value)}
                      onChange={(e) => (label === 'Start' ? setStart : setEnd)(Number(e.target.value))}
                      className="h-1.5 min-w-0 flex-1 accent-[#f5c542]"
                    />
                    <span className="w-10 text-right tabular-nums">{tickClock(value)}</span>
                  </label>
                );
              })}
              <p className="text-[10.5px] text-muted">
                {valid
                  ? `${tickClock(end)} → ${tickClock(start)} · repeats${start > end ? ' across dawn' : ''}`
                  : 'Choose different start and end times.'}
              </p>
            </>
          ) : null}
          {changed ? (
            <button
              type="button"
              disabled={!valid || disabled || saving}
              onClick={() => void save()}
              className="rounded border border-border bg-surface-2 px-2 py-1 text-xs hover:bg-surface-3 disabled:opacity-50"
            >
              {saving ? 'Saving…' : 'Apply time range'}
            </button>
          ) : null}
        </fieldset>
      ) : null}
    </div>
  );
}

/** Shared across the bill list and editor; lower numbers receive supplies first. */
export function BillPrioritySelect({ value, onChange, label = 'Bill priority', disabled = false }: {
  value: number;
  onChange: (priority: number) => void;
  label?: string;
  disabled?: boolean;
}) {
  return (
    <select
      aria-label={label}
      title="Priority across workbenches and fuel allocation (1 is highest)"
      value={value}
      disabled={disabled}
      onChange={(e) => onChange(Number(e.target.value))}
      className="h-6 shrink-0 rounded border border-border bg-surface px-0.5 text-[10.5px]"
    >
      <option value={1}>1 · Urgent</option>
      <option value={2}>2 · High</option>
      <option value={3}>3 · Normal</option>
      <option value={4}>4 · Low</option>
    </select>
  );
}

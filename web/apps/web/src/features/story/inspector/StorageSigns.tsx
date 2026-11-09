'use client';

import type { StoryStorage } from '@mineclaw/contracts';
import { useId, useState } from 'react';
import { Button, Input } from '@/components/ui/primitives';
import { filterSummary, PRIORITY_LABELS, type TreeNode } from '../storage';

/** The effective sign rules stay visible while the requested replacement waits for physical removal. */
export function StorageSigns({ storages, tree }: { storages: StoryStorage[]; tree: TreeNode[] }) {
  const controlled = storages.filter((s) => s.signs?.length || s.pendingFilter || s.pendingPriority);
  if (!controlled.length) return null;
  return (
    <div className="space-y-2 rounded border border-border bg-surface-2 p-2" data-testid="storage-sign-status">
      {controlled.map((s) => (
        <div key={s.chest.join(',')}>
          {storages.length > 1 ? <p className="font-medium">{s.label || `Chest ${s.chest.join(', ')}`}</p> : null}
          {s.signs?.length ? (
            <>
              <p className="font-medium">Active filter from attached signs: {filterSummary(tree, s.filter)}</p>
              <p className="text-muted-foreground">Sign priority: highest; matching items move here as space allows.</p>
              <p className="text-muted-foreground">Sign text searches item names: wood also matches wooden tools. Use commas or separate lines for alternatives, and - or ! to exclude. Categories such as food still work.</p>
              <details>
                <summary className="cursor-pointer text-muted-foreground">Attached signs ({s.signs.length})</summary>
                {s.signs.map((sign) => (
                  <p className="whitespace-pre-line pl-2" key={sign.at.join(',')}>
                    {sign.lines.filter(Boolean).join('\n')}
                    <span className="block text-muted-foreground">at {sign.at.join(', ')}</span>
                  </p>
                ))}
              </details>
            </>
          ) : null}
          {s.pendingFilter || s.pendingPriority ? (
            <p role="status" className="text-amber-300">
              Waiting for a pawn to remove the signs.
              {s.pendingFilter ? ` Requested filter (not active): ${filterSummary(tree, s.pendingFilter)}.` : ''}
              {s.pendingPriority ? ` Requested priority (not active): ${PRIORITY_LABELS[s.pendingPriority]}.` : ''}
            </p>
          ) : (
            <p className="text-muted-foreground">
              Changing this filter or priority queues sign removal. The new settings apply after the signs are gone.
            </p>
          )}
        </div>
      ))}
    </div>
  );
}

/** Entry changes apply independently to every selected chest, preserving its other settings. */
export function StorageEntryActions({
  tree,
  canWrite,
  apply,
}: {
  tree: TreeNode[];
  canWrite: boolean;
  apply: (op: 'add' | 'remove_entries', entries: string) => void;
}) {
  const [entries, setEntries] = useState('');
  const listId = useId();
  const categories: TreeNode[] = [];
  const visit = (nodes: TreeNode[]) =>
    nodes.forEach((node) => {
      categories.push(node);
      visit(node.children);
    });
  visit(tree);
  return (
    <details className="rounded border border-border p-2">
      <summary className="cursor-pointer">Allow or exclude entries on selected chests</summary>
      <p className="my-1 text-muted-foreground">
        Keeps each chest’s other filter settings. Separate categories or item IDs with commas.
      </p>
      <Input
        aria-label="Storage filter entries"
        list={listId}
        value={entries}
        onChange={(e) => setEntries(e.target.value)}
        placeholder="food, wood, 260:0"
        className="h-7 text-xs"
      />
      <datalist id={listId}>
        {categories.map((node) => (
          <option key={node.id} value={node.id}>
            {node.label}
          </option>
        ))}
      </datalist>
      <div className="mt-1 flex gap-1">
        <Button size="sm" disabled={!canWrite || !entries.trim()} onClick={() => apply('add', entries)}>
          Allow entries
        </Button>
        <Button size="sm" disabled={!canWrite || !entries.trim()} onClick={() => apply('remove_entries', entries)}>
          Exclude entries
        </Button>
      </div>
    </details>
  );
}

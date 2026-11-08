'use client';

import type { BotStatusView, HiveTreeNode } from '@mineclaw/contracts';
import { Brain, Code2, Crown, GitBranch, Network } from 'lucide-react';
import Link from 'next/link';
import { useHiveTree, useStatus } from '@/lib/live/hooks';
import { Badge, Card, CardHeader, EmptyState, ErrorState, SkeletonLines } from '@/components/ui/primitives';
import { flattenHive, KIND_LABEL } from './common';
import { mindColor, nodeActivity } from './hiveLook';

const kindIcon = { main: Crown, hive: Brain, subhive: GitBranch, script: Code2 } as const;

/** Whose brain drives a body: its owner's (a subhive), its own (a hive). */
function BrainNote({ node }: { node: HiveTreeNode }) {
  if (node.kind === 'subhive' && node.owner)
    return <span className="text-subtle"> · driven by {node.owner}’s brain</span>;
  return node.kind === 'hive' && node.brain ? <span className="text-subtle"> · own brain</span> : null;
}

function Row({
  node,
  depth,
  last,
  live,
}: {
  node: HiveTreeNode;
  depth: number;
  last: boolean;
  live: ReadonlyMap<string, BotStatusView>;
}) {
  const Icon = kindIcon[node.kind];
  const mind = node.kind === 'main' || node.kind === 'hive';
  const activity = nodeActivity(node, live.get(node.name));
  const color = mind ? mindColor(node) : activity.color;
  return (
    <>
      <li
        role="treeitem"
        aria-level={depth + 1}
        aria-expanded={node.children?.length ? true : undefined}
        aria-selected={false}
      >
        <Link
          href={`/live/${encodeURIComponent(node.name)}`}
          className="group flex items-center gap-3 rounded-lg px-3 py-2.5 hover:bg-surface-2 focus-visible:outline-2"
          style={{ paddingLeft: `${12 + depth * 28}px` }}
        >
          {depth > 0 ? (
            <span aria-hidden className="-ml-4 w-3 text-subtle">
              {last ? '└' : '├'}
            </span>
          ) : null}
          <span
            className="grid size-7 shrink-0 place-items-center rounded-lg"
            style={{ color, backgroundColor: `${color}20` }}
            title={mind ? `${node.kind === 'main' ? 'Main' : 'Hive'} mind` : activity.label}
            data-testid={`hive-icon-${node.name}`}
            data-work={mind ? undefined : activity.kind}
          >
            {mind ? (
              <Icon className="size-3.5" />
            ) : (
              <svg viewBox="0 0 16 16" className="size-4" aria-hidden>
                <path
                  d={activity.path}
                  fill="currentColor"
                  fillRule="evenodd"
                  stroke={activity.kind === 'attack' ? 'currentColor' : undefined}
                  strokeWidth={1.2}
                />
              </svg>
            )}
          </span>
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span
                className="truncate text-sm font-medium"
                style={{ color: mind ? color : undefined }}
                title={node.name}
              >
                {mind ? node.name : (node.name.match(/\d+$/)?.[0] ?? node.name)}
              </span>
              <Badge>{KIND_LABEL[node.kind]}</Badge>
            </div>
            <div className="truncate text-xs text-muted">
              <span style={{ color: mind ? undefined : activity.color }}>{activity.label}</span>
              <BrainNote node={node} />
            </div>
          </div>
          <span className="hidden text-xs text-subtle sm:block">{node.state}</span>
        </Link>
      </li>
      {(node.children ?? []).map((c, i, a) => (
        <Row key={c.id} node={c} depth={depth + 1} last={i === a.length - 1} live={live} />
      ))}
    </>
  );
}

export function HiveTreeView() {
  const tree = useHiveTree();
  const status = useStatus();
  const live = new Map((status.data?.bots ?? []).map((body) => [body.name, body]));
  const all = flattenHive(tree.data);
  const counts = all.slice(1).reduce<Record<string, number>>((m, n) => ({ ...m, [n.kind]: (m[n.kind] ?? 0) + 1 }), {});
  const summary =
    all.length <= 1
      ? 'Main alone, no clones'
      : `${all.length - 1} clone${all.length === 2 ? '' : 's'}: ${['hive', 'subhive', 'script'].map((k) => `${counts[k] ?? 0} ${k}`).join(', ')}`;
  return (
    <Card>
      <CardHeader title="Hive tree" subtitle={tree.data ? summary : undefined} icon={<Network className="size-4" />} />
      <div className="p-2">
        {tree.isPending ? (
          <SkeletonLines lines={4} className="p-3" />
        ) : tree.error ? (
          <ErrorState error={tree.error} onRetry={() => void tree.refetch()} />
        ) : tree.data ? (
          <ul role="tree" aria-label="Hive hierarchy">
            <Row node={tree.data} depth={0} last live={live} />
          </ul>
        ) : (
          <EmptyState title="No hive" />
        )}
      </div>
    </Card>
  );
}

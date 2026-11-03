'use client';

import type { HiveTreeNode } from '@mineclaw/contracts';
import { Brain, Code2, Crown, GitBranch, Network } from 'lucide-react';
import Link from 'next/link';
import { useHiveTree } from '@/lib/live/hooks';
import { cn } from '@/lib/utils';
import { Badge, Card, CardHeader, EmptyState, ErrorState, SkeletonLines } from '@/components/ui/primitives';
import { flattenHive, KIND_LABEL } from './common';

const kindIcon = { main: Crown, hive: Brain, subhive: GitBranch, script: Code2 } as const;

function Row({ node, depth, last }: { node: HiveTreeNode; depth: number; last: boolean }) {
  const Icon = kindIcon[node.kind];
  return (
    <>
      <li role="treeitem" aria-level={depth + 1} aria-expanded={node.children?.length ? true : undefined} aria-selected={false}>
        <Link
          href={`/live/${encodeURIComponent(node.name)}`}
          className="group flex items-center gap-3 rounded-lg px-3 py-2.5 hover:bg-surface-2 focus-visible:outline-2"
          style={{ paddingLeft: `${12 + depth * 28}px` }}
        >
          {depth > 0 ? <span aria-hidden className="-ml-4 w-3 text-subtle">{last ? '└' : '├'}</span> : null}
          <span className={cn('grid size-7 shrink-0 place-items-center rounded-lg', node.kind === 'main' ? 'bg-accent-soft text-accent' : 'bg-surface-2 text-muted')}>
            <Icon className="size-3.5" />
          </span>
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span className="truncate text-sm font-medium text-fg">{node.name}</span>
              <Badge>{KIND_LABEL[node.kind]}</Badge>
            </div>
            <div className="truncate text-xs text-muted">
              {node.task ?? 'idle'}
              {node.kind === 'subhive' && node.owner ? <span className="text-subtle"> · driven by {node.owner}’s brain</span> : null}
              {node.kind === 'hive' && node.brain ? <span className="text-subtle"> · own brain</span> : null}
            </div>
          </div>
          <span className="hidden text-xs text-subtle sm:block">{node.state}</span>
        </Link>
      </li>
      {(node.children ?? []).map((c, i, a) => (
        <Row key={c.id} node={c} depth={depth + 1} last={i === a.length - 1} />
      ))}
    </>
  );
}

export function HiveTreeView() {
  const tree = useHiveTree();
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
            <Row node={tree.data} depth={0} last />
          </ul>
        ) : (
          <EmptyState title="No hive" />
        )}
      </div>
    </Card>
  );
}

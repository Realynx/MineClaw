'use client';

/**
 * The shell's update notice: "MineClaw vX is available (you run vY)" for every signed-in user, dismissible per release in this
 * browser, with the release notes behind an expander. Admins also get "Update now" (systemd) or the upgrade command (docker,
 * helm), and the updater's progress and result.
 */
import { ArrowUpCircle, ChevronDown, ExternalLink, X } from 'lucide-react';
import { useState } from 'react';
import { Markdown } from '@/components/ui/Markdown';
import { Button } from '@/components/ui/primitives';
import { useAuth } from '@/lib/auth';
import { cn } from '@/lib/utils';
import { recentResult, RunStatus, updateActive, UpdateNowButton, UpgradeCommand, useDismissed, useUpdateStatus } from './update';

export function UpdateBanner() {
  const { isAdmin } = useAuth();
  const q = useUpdateStatus(isAdmin);
  const [dismissed, dismiss] = useDismissed();
  const [notesOpen, setNotesOpen] = useState(false);
  const [commandOpen, setCommandOpen] = useState(false);
  const s = q.data;
  if (!s?.enabled) return null;
  const latest = s.latest;
  const result = isAdmin ? recentResult(s) : null;
  const resultKey = result ? `${result.version ?? ''}@${result.finishedAt ?? result.startedAt ?? ''}` : '';
  const showRun = isAdmin && (updateActive(s) || (!!result && dismissed.result !== resultKey));
  const showRelease = !!latest && s.outdated && (dismissed.version !== latest.version || updateActive(s));
  if (!showRun && !showRelease) return null;

  return (
    <section
      aria-label="MineClaw update"
      className="shrink-0 border-b border-border bg-accent-soft px-4 py-2 text-sm text-fg sm:px-6"
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <ArrowUpCircle className="size-4 shrink-0 text-accent" aria-hidden />
        <div className="min-w-0 flex-1">
          {showRelease && latest ? (
            <p>
              <span className="font-medium">MineClaw v{latest.version} is available</span>{' '}
              <span className="text-muted">(you run v{s.current})</span>
            </p>
          ) : null}
          {showRun ? <RunStatus status={s} /> : null}
          {q.isError && updateActive(s) ? (
            <p className="text-xs text-muted">The server is restarting; checking again in a few seconds.</p>
          ) : null}
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          {showRelease && latest ? (
            <Button variant="ghost" size="sm" aria-expanded={notesOpen} onClick={() => setNotesOpen((o) => !o)}>
              Release notes
              <ChevronDown className={cn('size-3.5 transition-transform', notesOpen && 'rotate-180')} aria-hidden />
            </Button>
          ) : null}
          {isAdmin && showRelease ? <UpdateNowButton status={s} /> : null}
          {isAdmin && showRelease && s.update.mode === 'command' && s.update.command ? (
            <Button variant="secondary" size="sm" aria-expanded={commandOpen} onClick={() => setCommandOpen((o) => !o)}>
              How to update
            </Button>
          ) : null}
          {!updateActive(s) ? (
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label={showRelease && latest ? `Hide until the release after v${latest.version}` : 'Hide update result'}
              onClick={() => {
                const patch: { version?: string; result?: string } = {};
                if (showRelease && latest) patch.version = latest.version;
                if (result) patch.result = resultKey;
                dismiss(patch);
              }}
            >
              <X className="size-4" />
            </Button>
          ) : null}
        </div>
      </div>
      {commandOpen && s.update.command && showRelease ? (
        <div className="mt-2 space-y-1">
          <p className="text-xs text-muted">
            Run this on the {s.installKind === 'helm' ? 'machine with cluster access' : 'Docker host'}:
          </p>
          <UpgradeCommand command={s.update.command} />
        </div>
      ) : null}
      {notesOpen && latest && showRelease ? (
        <div className="mt-2 max-h-72 overflow-auto rounded-md border border-border bg-surface p-3">
          <p className="mb-2 font-medium">{latest.name}</p>
          {latest.notes.trim() ? <Markdown source={latest.notes} /> : <p className="text-muted">No release notes.</p>}
          <a
            href={latest.url}
            target="_blank"
            rel="noopener noreferrer"
            className="mt-3 inline-flex items-center gap-1 text-xs text-accent hover:underline"
          >
            {latest.notesTruncated ? 'Read the full notes on GitHub' : 'View the release on GitHub'}
            <ExternalLink className="size-3" aria-hidden />
          </a>
        </div>
      ) : null}
    </section>
  );
}

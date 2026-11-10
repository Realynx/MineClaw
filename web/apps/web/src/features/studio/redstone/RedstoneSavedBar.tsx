/**
 * A saved redstone design's bar. It offers no build: the pawns cannot build a working circuit until the Story hand-off
 * for circuits exists, and the bot's builder places none of its parts (no dust, repeaters, levers or pistons).
 */
import Link from 'next/link';

export function RedstoneSavedBar({
  saved,
  dirty,
}: {
  saved: { designId: number; rev: number; text: string };
  /** Changed since that save: the bar names the design instead of the save's reply, as other kinds do. */
  dirty?: boolean;
}) {
  return (
    <div
      className="flex flex-wrap items-center gap-2 border-b border-border bg-ok-soft px-3 py-1.5 text-xs text-ok"
      data-testid="saved-bar"
    >
      <span>
        {(!dirty && saved.text) || `Design #${saved.designId}${saved.rev ? ` r${saved.rev}` : ''}`}
        {dirty ? ' · edited since' : ''}
      </span>
      <span className="ml-auto" />
      <span className="text-muted" data-testid="redstone-build-note">
        Pawns can't build circuits yet
      </span>
      <Link href={`/designs?id=${saved.designId}`} className="text-ok underline">
        Open in Designs
      </Link>
    </div>
  );
}

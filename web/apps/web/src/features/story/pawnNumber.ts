/** Presentation only: the numeric suffix of a known colony pawn, never a protocol/player identity replacement. */
export function pawnNumber(name: string): string {
  return name.match(/\d+$/)?.[0] ?? '';
}

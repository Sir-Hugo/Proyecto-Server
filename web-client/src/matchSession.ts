export type MatchCursor = { matchId: string | null; sequence: number }
export type SequencedMatchMessage = { matchId: string; sequence: number }

export function advanceMatchCursor(
  cursor: MatchCursor,
  expectedMatchId: string | null,
  message: SequencedMatchMessage,
  allowEqualSequence = false,
): { accepted: boolean; cursor: MatchCursor } {
  if (expectedMatchId && message.matchId !== expectedMatchId)
    return { accepted: false, cursor }

  if (cursor.matchId !== message.matchId)
    return { accepted: true, cursor: { matchId: message.matchId, sequence: message.sequence } }

  const accepted = allowEqualSequence
    ? message.sequence >= cursor.sequence
    : message.sequence > cursor.sequence
  return { accepted, cursor: accepted ? { matchId: message.matchId, sequence: message.sequence } : cursor }
}

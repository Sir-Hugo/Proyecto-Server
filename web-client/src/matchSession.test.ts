import assert from 'node:assert/strict'
import test from 'node:test'
import { advanceMatchCursor } from './matchSession.ts'

test('a new match starts its own sequence and replaces the completed match cursor', () => {
  const previous = { matchId: 'first-match', sequence: 6 }
  const result = advanceMatchCursor(previous, 'second-match', { matchId: 'second-match', sequence: 0 }, true)

  assert.equal(result.accepted, true)
  assert.deepEqual(result.cursor, { matchId: 'second-match', sequence: 0 })
})

test('a late response from the previous match cannot replace the selected match', () => {
  const current = { matchId: 'second-match', sequence: 0 }
  const result = advanceMatchCursor(current, 'second-match', { matchId: 'first-match', sequence: 7 }, true)

  assert.equal(result.accepted, false)
  assert.deepEqual(result.cursor, current)
})

test('out-of-order events from the same match are rejected', () => {
  const current = { matchId: 'second-match', sequence: 3 }

  assert.equal(advanceMatchCursor(current, 'second-match', { matchId: 'second-match', sequence: 2 }).accepted, false)
  assert.equal(advanceMatchCursor(current, 'second-match', { matchId: 'second-match', sequence: 3 }).accepted, false)
  assert.equal(advanceMatchCursor(current, 'second-match', { matchId: 'second-match', sequence: 4 }).accepted, true)
})

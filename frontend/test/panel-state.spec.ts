// @vitest-environment nuxt
import { describe, expect, it } from 'vitest'
import { describePanel } from '~/composables/usePanelState'
import type { PanelState } from '~/types/api'

/**
 * The words a panel is described with. There are five, and the order they are checked in is the
 * point: an operator in an incident reads the badge and nothing else, so "nothing replicated" has
 * to be distinguishable from "replicating" at a glance - the first cannot take the fleet over and
 * the second can.
 */
describe('describePanel', () => {
  it('names the panel that is in charge', () => {
    expect(describePanel('active').word).toBe('In charge')
  })

  it('keeps a standby that holds nothing apart from one that is up to date', () => {
    expect(describePanel('never_synced').word).not.toBe(describePanel('replicating').word)
  })

  it('treats a standby holding no fleet state as something to act on', () => {
    expect(describePanel('never_synced').tone).toBe('warning')
  })

  it('reads losing contact as an error rather than a quieter standby', () => {
    expect(describePanel('silent').tone).toBe('error')
  })

  it('does not colour a panel that was cut off on purpose', () => {
    expect(describePanel('revoked').tone).toBe('muted')
  })

  it('gives every state a word and an icon', () => {
    const states: PanelState[] = ['active', 'replicating', 'never_synced', 'silent', 'revoked']

    for (const state of states) {
      const described = describePanel(state)
      expect(described.word).toBeTruthy()
      expect(described.icon).toMatch(/^i-lucide-/)
    }
  })
})

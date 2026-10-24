// @vitest-environment nuxt
import { describe, expect, it } from 'vitest'
import { describePanel, describeRound } from '~/composables/usePanelState'
import type { PanelFailoverAction, PanelState } from '~/types/api'

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

/**
 * The same verdict means two different things depending on which panel reached it. The panel in
 * charge borrows the promotion policy's vocabulary to report on the pair, so reading the
 * standby's word out there tells an operator the opposite of what happened.
 */
describe('describeRound', () => {
  it('does not tell the panel in charge it will not take over', () => {
    const asStandby = describeRound('refused', 'standby')
    const asActive = describeRound('refused', 'active')

    expect(asStandby.word).toBe('Will not take over')
    expect(asActive.word).toBe('Pair degraded')
  })

  it('reads a quiet round as the pair being healthy when this panel is in charge', () => {
    expect(describeRound('none', 'active').word).toBe('Pair healthy')
    expect(describeRound('none', 'active').tone).toBe('success')
  })

  it('keeps the standby wording for a standby', () => {
    expect(describeRound('none', 'standby').word).toBe('Nothing to do')
    expect(describeRound('recommend', 'standby').word).toBe('Take over by hand')
  })

  /** The round that took over reports itself as active, and the word is right either way. */
  it('says the same thing about a promotion in both roles', () => {
    expect(describeRound('promote', 'active')).toEqual(describeRound('promote', 'standby'))
    expect(describeRound('promote', 'active').word).toBe('Took over')
  })

  it('gives every action a word and an icon in both roles', () => {
    const actions: PanelFailoverAction[] = ['none', 'wait', 'hold', 'recommend', 'refused', 'promote']

    for (const action of actions) {
      for (const role of ['active', 'standby'] as const) {
        const described = describeRound(action, role)
        expect(described.word).toBeTruthy()
        expect(described.icon).toMatch(/^i-lucide-/)
      }
    }
  })
})

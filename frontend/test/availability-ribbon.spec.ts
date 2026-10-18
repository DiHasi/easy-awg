import { describe, expect, it } from 'vitest'
import { mountSuspended } from '@nuxt/test-utils/runtime'
import AvailabilityRibbon from '~/components/AvailabilityRibbon.vue'
import type { NodeAvailabilityPoint } from '~/types/api'

/**
 * The availability ribbon, mounted rather than reimplemented, because its two rules are the ones
 * that decide whether the drawing tells the truth:
 *
 * an hour with no samples is the panel not having been running, not the node having been down;
 * and an hour that was bad for part of itself is drawn as bad, because averaging it away is how a
 * flapping node comes to look fine.
 */
function point(at: string, counts: Partial<NodeAvailabilityPoint> = {}): NodeAvailabilityPoint {
  const filled = { samples: 0, healthy: 0, blocked: 0, down: 0, silent: 0, unknown: 0, ...counts }
  return { at, ...filled }
}

async function ribbon(points: NodeAvailabilityPoint[]) {
  const mounted = await mountSuspended(AvailabilityRibbon, {
    props: { points, bucket: 'hour' as const }
  })

  return mounted.findAll('[title]')
}

describe('AvailabilityRibbon', () => {
  it('draws an unrecorded bucket as a gap rather than as a failure', async () => {
    const segments = await ribbon([point('2026-03-02T12:00:00+00:00')])
    const segment = segments[0]!

    expect(segment.attributes('title')).toContain('not recorded')
    // No health colour of any kind: colouring it would be a claim the record does not support.
    expect(segment.classes().some(name => name.startsWith('bg-success') || name.startsWith('bg-error'))).toBe(false)
  })

  it('draws an hour that was healthy throughout as healthy', async () => {
    const segments = await ribbon([point('2026-03-02T12:00:00+00:00', { samples: 120, healthy: 120 })])
    const segment = segments[0]!

    expect(segment.attributes('title')).toContain('healthy throughout')
    expect(segment.classes()).toContain('bg-success')
  })

  // One blocked round in two hours of them is still an hour that was blocked.
  it('shows the worst state an hour contained, not the most common one', async () => {
    const segments = await ribbon([point('2026-03-02T12:00:00+00:00', { samples: 120, healthy: 119, blocked: 1 })])
    const segment = segments[0]!

    expect(segment.attributes('title')).toContain('blocked at some point')
    expect(segment.classes()).toContain('bg-error')
    // And it still says how much of the hour was fine, so the ribbon is not alarmist either.
    expect(segment.attributes('title')).toContain('119/120 healthy')
  })

  it('ranks blocked above down and down above silent', async () => {
    const at = '2026-03-02T12:00:00+00:00'
    const blocked = await ribbon([point(at, { samples: 3, blocked: 1, down: 1, silent: 1 })])
    const down = await ribbon([point(at, { samples: 2, down: 1, silent: 1 })])
    const silent = await ribbon([point(at, { samples: 2, silent: 1, healthy: 1 })])

    expect(blocked[0]!.attributes('title')).toContain('blocked')
    expect(down[0]!.attributes('title')).toContain('down')
    expect(silent[0]!.attributes('title')).toContain('silent')
  })

  it('draws one segment per bucket, so the row spans the whole window', async () => {
    const segments = await ribbon([
      point('2026-03-02T12:00:00+00:00', { samples: 1, healthy: 1 }),
      point('2026-03-02T13:00:00+00:00'),
      point('2026-03-02T14:00:00+00:00', { samples: 1, down: 1 })
    ])

    expect(segments).toHaveLength(3)
  })
})

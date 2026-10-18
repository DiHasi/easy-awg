import { describe, expect, it } from 'vitest'
import { addressOrder, formatBucket, formatBucketLong, formatBytes, formatShare, shortKey } from '~/composables/useFormat'

/**
 * The formatters every sheet reads through. Small functions, but each of them has a case where the
 * obvious implementation says something untrue: a byte count that rounds a kilobyte to "0 MB", a
 * share of nothing that reads as 0%, a tunnel address that sorts .12 before .9.
 */
describe('formatBytes', () => {
  it('says nothing moved rather than dividing by a log of zero', () => {
    expect(formatBytes(0)).toBe('0 B')
  })

  it('keeps bytes whole and gives everything larger one decimal', () => {
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(1536)).toBe('1.5 KB')
    expect(formatBytes(5 * 1024 ** 3)).toBe('5.0 GB')
  })

  it('stops at terabytes rather than naming a unit nobody reads', () => {
    expect(formatBytes(1024 ** 5)).toBe('1024.0 TB')
  })
})

describe('formatShare', () => {
  // A share of nothing is zero, not NaN: an empty window is a normal thing to look at.
  it('is zero when there is nothing to take a share of', () => {
    expect(formatShare(0, 0)).toBe('0%')
    expect(formatShare(5, 0)).toBe('0%')
  })

  // Rounding this one down to 0% would say a peer moved nothing when it moved something.
  it('never rounds a small share away to nothing', () => {
    expect(formatShare(1, 1000)).toBe('<1%')
    expect(formatShare(0, 1000)).toBe('0%')
  })

  it('reads a ratio out of 1 as a percentage, which is how uptime is served', () => {
    expect(formatShare(1, 1)).toBe('100%')
    expect(formatShare(0.993, 1)).toBe('99%')
  })
})

describe('formatBucket', () => {
  it('labels an hourly bucket with the time and a daily one with the date', () => {
    const at = '2026-03-02T21:00:00+00:00'
    expect(formatBucketLong(at, 'day')).toBe(formatBucket(at, 'day'))
    expect(formatBucketLong(at, 'hour')).toContain(formatBucket(at, 'hour'))
  })

  it('prints the minutes rather than rounding them away, because :45 zones are real', () => {
    expect(formatBucket('2026-03-02T21:00:00+00:00', 'hour')).toMatch(/^\d{2}:\d{2}$/)
  })
})

describe('addressOrder', () => {
  // The peer list is ordered by this, and a string sort puts .12 between .1 and .2.
  it('orders tunnel addresses numerically', () => {
    expect(addressOrder('10.8.0.12/32')).toBeGreaterThan(addressOrder('10.8.0.9/32'))
    expect(addressOrder('10.8.0.2/32')).toBeGreaterThan(addressOrder('10.8.0.1/32'))
  })
})

describe('shortKey', () => {
  it('keeps both ends of a key, which is what makes one recognisable', () => {
    const key = 'abcdefghijklmnopqrstuvwxyz0123456789=='
    const short = shortKey(key)

    expect(short.startsWith('abcdefgh')).toBe(true)
    expect(short.endsWith('6789==')).toBe(true)
    expect(short).toContain('…')
  })

  it('leaves a short key alone and has something to print for nothing', () => {
    expect(shortKey('short')).toBe('short')
    expect(shortKey(null)).toBe('—')
  })
})

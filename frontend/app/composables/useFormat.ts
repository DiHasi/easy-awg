/** Formatting shared by every sheet, so a handshake reads the same wherever it appears. */

export function formatBytes(bytes: number) {
  if (!bytes) {
    return '0 B'
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1)
  return `${(bytes / 1024 ** index).toFixed(index === 0 ? 0 : 1)} ${units[index]}`
}

export function relativeTime(value?: string | null, never = 'never') {
  if (!value) {
    return never
  }

  const seconds = Math.max(0, Math.round((Date.now() - new Date(value).getTime()) / 1000))
  if (seconds < 60) {
    return `${seconds}s ago`
  }
  if (seconds < 3600) {
    return `${Math.round(seconds / 60)}m ago`
  }
  if (seconds < 86400) {
    return `${Math.round(seconds / 3600)}h ago`
  }
  return `${Math.round(seconds / 86400)}d ago`
}

/** Drawings are dated in UTC: an operator in another timezone reads the same sheet. */
export function formatUtc(value?: string | Date | null) {
  if (!value) {
    return '—'
  }

  const date = typeof value === 'string' ? new Date(value) : value
  return `${date.toISOString().slice(0, 16).replace('T', ' ')} UTC`
}

/**
 * The same stamp on the clock the reader is looking at, naming the zone so it cannot be mistaken
 * for the UTC one beside it. Used where the traffic history is read rather than compared: a
 * history is about the reader's own evenings, not about a shared coordinate.
 */
export function formatLocal(value?: string | Date | null) {
  if (!value) {
    return '—'
  }

  // The year is carried because these are the two stamps that can be old: retention reaches a
  // year, and a counter reset is as old as the peer.
  const date = typeof value === 'string' ? new Date(value) : value
  return date.toLocaleString('en-GB', {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
    timeZoneName: 'short'
  })
}

/** The IANA name of the zone the page is being read in, for copy that has to say which one. */
export function localZone() {
  return Intl.DateTimeFormat().resolvedOptions().timeZone
}

/**
 * Today as the reader's own calendar has it, ISO-ordered so it sorts. Naming a downloaded file
 * after the UTC date would date it yesterday for anyone reading past their own midnight.
 */
export function localDate(date = new Date()) {
  const month = String(date.getMonth() + 1).padStart(2, '0')
  return `${date.getFullYear()}-${month}-${String(date.getDate()).padStart(2, '0')}`
}

/**
 * The reader's offset east of UTC, the ISO reading of it: +03:00 is 180. JavaScript reports the
 * opposite sign, so the flip happens here rather than at each of the three fetches that send it.
 */
export function utcOffsetMinutes() {
  return -new Date().getTimezoneOffset()
}

/** Keys are recognisable by their ends; the middle only makes a row wrap. */
export function shortKey(key?: string | null) {
  if (!key) {
    return '—'
  }

  return key.length > 16 ? `${key.slice(0, 8)}…${key.slice(-6)}` : key
}

/** Sort key for "10.8.0.12/32" so .12 lands after .9 rather than after .1. */
export function addressOrder(address: string) {
  return address
    .split('/')[0]!
    .split('.')
    .reduce((total, octet) => total * 256 + (Number(octet) || 0), 0)
}

export function itemNumber(index: number) {
  return String(index + 1).padStart(2, '0')
}

/**
 * A bucket's own label: the hour it starts, or the day. Both on the reader's own clock, unlike
 * every other time on a sheet - a history answers which evening the node was saturated, and an
 * evening three hours off from the one they remember is an answer they have to convert first.
 *
 * The day is safe to read locally because the panel asks for the series bucketed by the reader's
 * days in the first place. The hour is an instant on a UTC boundary, so in a zone offset by :30
 * or :45 it genuinely starts at half past - printed, not rounded away.
 *
 * The locale stays pinned while the zone does not: it is what keeps "5 Oct" from reordering
 * itself per browser and the axis column from changing width under the numbers.
 */
export function formatBucket(value: string, bucket: 'hour' | 'day') {
  const date = new Date(value)
  if (bucket === 'day') {
    return date.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })
  }

  return date.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false })
}

/** The same instant, dated as well as timed: what a hovered bucket needs to be unambiguous. */
export function formatBucketLong(value: string, bucket: 'hour' | 'day') {
  const day = new Date(value).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })
  return bucket === 'day' ? day : `${day} ${formatBucket(value, 'hour')}`
}

/** A share of a total, as a percentage string. Zero of zero is zero, not NaN. */
export function formatShare(value: number, total: number) {
  if (!total) {
    return '0%'
  }

  const share = (value / total) * 100
  return share > 0 && share < 1 ? '<1%' : `${Math.round(share)}%`
}

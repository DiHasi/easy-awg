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

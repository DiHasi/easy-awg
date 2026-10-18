<script setup lang="ts">
import type { NodeAvailabilityPoint, UsageBucket } from '~/types/api'

/**
 * One node's history as a row of buckets, each coloured by what dominated it.
 *
 * Hand-drawn in divs for the same reason as the traffic chart: the panel ships as static files and
 * a charting dependency is weight on every page load. A ribbon rather than bars because the
 * question is not "how much" but "when was it bad", and a shape you can scan in one pass across
 * every node is what answers it.
 *
 * A bucket nothing was recorded in is drawn as a gap - flat canvas, no health colour - because the
 * panel not running is not the node being down, and colouring it either way would be a claim the
 * record does not support.
 *
 * The health words keep the colours they have everywhere else on the sheet, and `live` stays out
 * of it: that one is reserved for the path traffic actually takes.
 */
const props = withDefaults(defineProps<{
  points: NodeAvailabilityPoint[]
  bucket: UsageBucket
  compact?: boolean
}>(), {
  compact: false
})

const hovered = ref<number | null>(null)

type Segment = {
  at: string
  /** Null for a bucket with no samples in it. */
  state: 'healthy' | 'blocked' | 'down' | 'silent' | 'unknown' | null
  title: string
}

const fills: Record<NonNullable<Segment['state']>, string> = {
  healthy: 'bg-success',
  blocked: 'bg-error',
  down: 'bg-error/60',
  silent: 'bg-warning',
  unknown: 'bg-accented'
}

/**
 * What a bucket is drawn as. The worst state that occurred in it wins rather than the most
 * frequent one: an hour that was blocked for ten minutes is an hour worth seeing, and averaging it
 * away is exactly how a flapping node comes to look fine.
 */
function worst(point: NodeAvailabilityPoint): Segment['state'] {
  if (point.samples === 0) {
    return null
  }
  if (point.blocked > 0) {
    return 'blocked'
  }
  if (point.down > 0) {
    return 'down'
  }
  if (point.silent > 0) {
    return 'silent'
  }
  if (point.healthy > 0) {
    return 'healthy'
  }
  return 'unknown'
}

const words: Record<NonNullable<Segment['state']>, string> = {
  healthy: 'healthy throughout',
  blocked: 'blocked at some point',
  down: 'down at some point',
  silent: 'silent at some point',
  unknown: 'nothing vouched for it'
}

const segments = computed<Segment[]>(() => props.points.map((point) => {
  const state = worst(point)
  const when = formatBucketLong(point.at, props.bucket)

  return {
    at: point.at,
    state,
    title: state === null
      ? `${when} · not recorded`
      : `${when} · ${words[state]} (${point.healthy}/${point.samples} healthy)`
  }
}))

const hoveredSegment = computed(() => hovered.value === null ? null : segments.value[hovered.value] ?? null)

const recorded = computed(() => props.points.filter(point => point.samples > 0).length)

const label = computed(() => {
  const first = props.points.at(0)
  const last = props.points.at(-1)
  if (!first || !last) {
    return 'No history recorded'
  }

  return `Availability from ${formatBucketLong(first.at, props.bucket)} to ${formatBucketLong(last.at, props.bucket)}, `
    + `${recorded.value} of ${props.points.length} ${props.bucket}s recorded`
})
</script>

<template>
  <div class="flex flex-col gap-1.5">
    <div
      class="flex gap-px overflow-hidden rounded-sm"
      :class="compact ? 'h-2.5' : 'h-4'"
      role="img"
      :aria-label="label"
      @pointerleave="hovered = null"
    >
      <div
        v-for="(segment, index) in segments"
        :key="segment.at"
        class="flex-1 transition-opacity"
        :class="[
          segment.state ? fills[segment.state] : 'bg-elevated/60',
          hovered !== null && hovered !== index ? 'opacity-50' : ''
        ]"
        :title="segment.title"
        @pointerenter="hovered = index"
      />
    </div>

    <!-- The readout is the ribbon's axis: hovering names the bucket, and with nothing hovered the
         span stands where that label would, so the row never changes height. -->
    <p
      v-if="!compact"
      class="tabular min-h-4 font-mono text-[11px] text-dimmed"
    >
      <template v-if="hoveredSegment">
        {{ hoveredSegment.title }}
      </template>
      <template v-else-if="recorded === 0">
        no history recorded in this window
      </template>
      <template v-else>
        {{ formatBucket(points[0]!.at, bucket) }} → {{ formatBucket(points[points.length - 1]!.at, bucket) }}
      </template>
    </p>
  </div>
</template>

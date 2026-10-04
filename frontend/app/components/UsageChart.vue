<script setup lang="ts">
import type { UsageBucket, UsagePoint } from '~/types/api'

/**
 * Traffic over time, as bars.
 *
 * Hand-drawn rather than drawn by a charting library: the panel ships as static files served by
 * the control plane, and a dependency the size of a chart engine for one stacked bar chart is
 * weight on every page load. Divs, not SVG, so a bar keeps its own width at any viewport without
 * a viewBox stretching the stroke along with it.
 *
 * Download sits under upload in one hue rather than two: it is one measurement split in two
 * directions, not two unrelated series. The health colours and `live` stay out of it - those mean
 * something specific elsewhere on the sheet.
 */
const props = withDefaults(defineProps<{
  points: UsagePoint[]
  bucket: UsageBucket
  /** Shown while the first answer is still in flight, so the bars do not flash in empty. */
  loading?: boolean
  compact?: boolean
}>(), {
  loading: false,
  compact: false
})

const hovered = ref<number | null>(null)

const totals = computed(() => props.points.map(point => point.receivedBytes + point.transmittedBytes))

/**
 * The tallest bucket, which is what every bar is drawn against. Zero everywhere means an empty
 * chart, and dividing by it would make every bar full height.
 */
const peak = computed(() => Math.max(...totals.value, 1))

const moved = computed(() => totals.value.reduce((sum, value) => sum + value, 0))

/**
 * The node counts what it received from a peer, which is that peer's upload. The labels are the
 * person's way round - they think in what their own device pulled down - so the flip happens
 * here, once, exactly as the traffic dialog does it.
 */
const down = computed(() => props.points.reduce((sum, item) => sum + item.transmittedBytes, 0))
const up = computed(() => props.points.reduce((sum, item) => sum + item.receivedBytes, 0))

const point = computed(() => hovered.value === null ? null : props.points[hovered.value] ?? null)

/** A bucket with traffic in it is never drawn as nothing: 2px is "some", 0px is "none". */
function height(value: number) {
  if (value <= 0) {
    return '0'
  }

  return `max(2px, ${(value / peak.value) * 100}%)`
}

const label = computed(() => {
  const first = props.points.at(0)
  const last = props.points.at(-1)
  return first && last
    ? `Traffic from ${formatBucketLong(first.at, props.bucket)} to ${formatBucketLong(last.at, props.bucket)}, `
    + `${formatBytes(moved.value)} in total`
    : 'No traffic recorded'
})

/** Enough ticks to read the span by, never so many that they collide on a phone. */
const ticks = computed(() => {
  const count = props.points.length
  if (count === 0) {
    return []
  }

  const step = Math.max(1, Math.floor(count / (props.compact ? 2 : 5)))
  return props.points
    .map((item, index) => ({ index, at: item.at }))
    .filter(item => item.index % step === 0)
})
</script>

<template>
  <div class="flex flex-col gap-2">
    <!-- The readout is the chart's own axis: hovering a bar names it, and with nothing hovered
         the window's total stands where the bar's would be, so the row never jumps. -->
    <div class="flex min-h-5 flex-wrap items-baseline gap-x-3 gap-y-1 text-xs">
      <span class="tabular font-mono text-muted">
        <template v-if="point">{{ formatBucketLong(point.at, bucket) }}</template>
        <template v-else-if="moved === 0">nothing recorded in this window</template>
        <template v-else>{{ formatBytes(peak) }} peak per {{ bucket }}</template>
      </span>
      <span class="tabular ms-auto flex items-center gap-3 font-mono">
        <span class="flex items-center gap-1.5 text-highlighted">
          <span
            class="size-2 rounded-xs bg-primary"
            aria-hidden="true"
          />
          ↓ {{ formatBytes(point ? point.transmittedBytes : down) }}
        </span>
        <span class="flex items-center gap-1.5 text-highlighted">
          <span
            class="size-2 rounded-xs bg-primary/45"
            aria-hidden="true"
          />
          ↑ {{ formatBytes(point ? point.receivedBytes : up) }}
        </span>
      </span>
    </div>

    <div
      v-if="loading"
      class="flex items-center justify-center gap-2 border-b border-default text-sm text-muted"
      :class="compact ? 'h-20' : 'h-36'"
    >
      <UIcon
        name="i-lucide-loader-circle"
        class="size-4 animate-spin"
      />
      Reading the history
    </div>

    <div
      v-else
      class="flex items-end gap-px border-b border-default"
      :class="compact ? 'h-20' : 'h-36'"
      role="img"
      :aria-label="label"
      @pointerleave="hovered = null"
    >
      <div
        v-for="(item, index) in points"
        :key="item.at"
        class="flex h-full flex-1 flex-col justify-end transition-colors"
        :class="hovered === index ? 'bg-elevated' : 'hover:bg-elevated/60'"
        :title="`${formatBucketLong(item.at, bucket)} · ↓ ${formatBytes(item.transmittedBytes)} ↑ ${formatBytes(item.receivedBytes)}`"
        @pointerenter="hovered = index"
      >
        <!-- Upload above download, in one hue at two weights: the legend reads the same way. -->
        <div
          class="w-full bg-primary/45"
          :style="{ height: height(item.receivedBytes) }"
        />
        <div
          class="w-full bg-primary"
          :style="{ height: height(item.transmittedBytes) }"
        />
      </div>
    </div>

    <div
      v-if="!loading && ticks.length > 0"
      class="tabular flex justify-between font-mono text-[11px] text-dimmed"
      aria-hidden="true"
    >
      <span
        v-for="tick in ticks"
        :key="tick.at"
      >{{ formatBucket(tick.at, bucket) }}</span>
    </div>
  </div>
</template>

/**
 * Dragging rows and sections with pointer events, without a drag-and-drop library.
 *
 * Deliberately not the HTML5 drag-and-drop API: it does nothing at all on touch, and the peer
 * list is read from a phone as often as from a desk. Pointer events cover mouse, pen and touch
 * through the same three handlers, and capturing the pointer on the handle means the gesture
 * survives the cursor leaving the row it started on.
 *
 * The DOM is the map: a draggable row carries `data-sort-id` and `data-sort-kind`, sits inside an
 * element carrying `data-sort-bucket`, and what the pointer is over is resolved with
 * `elementFromPoint`. Nothing here knows what a peer or a group is - it reports "this id should
 * land in that bucket, before that id" and the caller decides what that means. The kind is part
 * of the markup because a section is both a bucket for peers and a row for groups, and a peer
 * dragged over a section's header must not be measured against the section.
 */

export type DragKind = 'peer' | 'group'

/** `beforeId` null means the end of the bucket, which is also where an empty one accepts a drop. */
export type DropTarget = {
  kind: DragKind
  bucket: string
  beforeId: string | null
}

type Dragged = {
  kind: DragKind
  id: string
  bucket: string
}

export function useDragSort(onDrop: (dragged: Dragged, target: DropTarget) => void) {
  const dragged = ref<Dragged | null>(null)
  const target = ref<DropTarget | null>(null)

  let handle: HTMLElement | null = null
  let pointerId: number | null = null
  let scrollVelocity = 0
  let scrollFrame: number | null = null

  const dragging = computed(() => dragged.value !== null)

  /** True for the row being carried, so it can be dimmed while it is in flight. */
  function isDragged(kind: DragKind, id: string) {
    return dragged.value?.kind === kind && dragged.value.id === id
  }

  /**
   * True where the drop line belongs: above this row, or at the end of the bucket for a null id.
   * `bucket` is compared only when given - a peer's "end of the list" means the end of one
   * bucket, while a section being carried over the list is not in a bucket at all.
   */
  function isDropBefore(kind: DragKind, id: string | null, bucket?: string) {
    const drop = target.value
    return drop !== null
      && drop.kind === kind
      && drop.beforeId === id
      && (bucket === undefined || drop.bucket === bucket)
  }

  function begin(event: PointerEvent, kind: DragKind, id: string, bucket: string) {
    // Only the primary button; a right-click on a handle should still open the context menu.
    if (event.button !== 0 || dragged.value) {
      return
    }

    handle = event.currentTarget as HTMLElement
    pointerId = event.pointerId
    handle.setPointerCapture(event.pointerId)

    dragged.value = { kind, id, bucket }
    target.value = null

    // Stops a touch from scrolling the page and a mouse from selecting the text it passes over.
    event.preventDefault()

    handle.addEventListener('pointermove', move)
    handle.addEventListener('pointerup', drop)
    handle.addEventListener('pointercancel', cancel)
    window.addEventListener('keydown', onKey)
  }

  function move(event: PointerEvent) {
    const carried = dragged.value
    if (!carried) {
      return
    }

    event.preventDefault()
    edgeScroll(event.clientY)

    const over = document.elementFromPoint(event.clientX, event.clientY)
    if (!over) {
      return
    }

    const bucketElement = over.closest<HTMLElement>('[data-sort-bucket]')
    const bucket = bucketElement?.dataset.sortBucket
    if (!bucket) {
      return
    }

    // A group is carried past whole sections, so what it is measured against is a section rather
    // than a peer inside one.
    const selector = `[data-sort-kind="${carried.kind}"]`
    const row = over.closest<HTMLElement>(selector)
    const rowId = row?.dataset.sortId

    if (!row || rowId === carried.id) {
      // Over the gap below the rows, or over the row being carried: a gap means the end of the
      // bucket, the carried row itself means "no decision yet", so the line stays where it was.
      if (!row) {
        target.value = { kind: carried.kind, bucket, beforeId: null }
      }
      return
    }

    const box = row.getBoundingClientRect()
    const below = event.clientY > box.top + (box.height / 2)
    target.value = {
      kind: carried.kind,
      bucket,
      beforeId: below ? nextId(row, selector) : rowId ?? null
    }
  }

  /** The row after this one in the same bucket, or null when it is the last - the end of the list. */
  function nextId(row: HTMLElement, selector: string) {
    let sibling = row.nextElementSibling
    while (sibling) {
      if (sibling instanceof HTMLElement && sibling.matches(selector)) {
        return sibling.dataset.sortId ?? null
      }
      sibling = sibling.nextElementSibling
    }
    return null
  }

  function drop() {
    const carried = dragged.value
    const landing = target.value
    finish()

    if (carried && landing && landing.kind === carried.kind) {
      onDrop(carried, landing)
    }
  }

  function cancel() {
    finish()
  }

  function onKey(event: KeyboardEvent) {
    // Escape is the way out of a gesture that was started by accident.
    if (event.key === 'Escape') {
      finish()
    }
  }

  function finish() {
    if (handle) {
      handle.removeEventListener('pointermove', move)
      handle.removeEventListener('pointerup', drop)
      handle.removeEventListener('pointercancel', cancel)
      if (pointerId !== null && handle.hasPointerCapture(pointerId)) {
        handle.releasePointerCapture(pointerId)
      }
    }

    window.removeEventListener('keydown', onKey)
    handle = null
    pointerId = null
    dragged.value = null
    target.value = null
    stopScrolling()
  }

  /**
   * Keeps scrolling while the pointer is held near the top or bottom of the window: a long peer
   * list does not fit on a screen, and a drag that cannot reach past the fold cannot reorder it.
   */
  function edgeScroll(y: number) {
    const margin = 80
    const top = margin - y
    const bottom = y - (window.innerHeight - margin)
    scrollVelocity = top > 0 ? -Math.min(top, margin) / 4 : bottom > 0 ? Math.min(bottom, margin) / 4 : 0

    if (scrollVelocity !== 0 && scrollFrame === null) {
      scrollFrame = requestAnimationFrame(scrollStep)
    }
  }

  function scrollStep() {
    scrollFrame = null
    if (!dragged.value || scrollVelocity === 0) {
      return
    }

    window.scrollBy(0, scrollVelocity)
    scrollFrame = requestAnimationFrame(scrollStep)
  }

  function stopScrolling() {
    if (scrollFrame !== null) {
      cancelAnimationFrame(scrollFrame)
      scrollFrame = null
    }
    scrollVelocity = 0
  }

  // A page left mid-drag must not leave a listener on a handle that is going away.
  onBeforeUnmount(finish)

  return { dragging, dragged, target, begin, isDragged, isDropBefore }
}

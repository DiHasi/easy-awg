/**
 * Runs `task` on mount and then every `intervalMs` until the component unmounts.
 *
 * A tick is skipped while the tab is hidden or while the previous tick is still in flight: a
 * slow panel should get fewer requests when it struggles, not a queue of them.
 */
export function usePolling(task: () => Promise<unknown>, intervalMs: number) {
  let timer: ReturnType<typeof setInterval> | null = null
  let running = false

  async function tick() {
    if (running || document.hidden) {
      return
    }

    running = true
    try {
      await task()
    } finally {
      running = false
    }
  }

  function onVisible() {
    if (!document.hidden) {
      void tick()
    }
  }

  onMounted(() => {
    void tick()
    timer = setInterval(tick, intervalMs)
    document.addEventListener('visibilitychange', onVisible)
  })

  onBeforeUnmount(() => {
    if (timer) {
      clearInterval(timer)
    }
    document.removeEventListener('visibilitychange', onVisible)
  })

  return { tick }
}

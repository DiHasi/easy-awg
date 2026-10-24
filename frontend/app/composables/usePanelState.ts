import type { PanelPair, PanelState } from '~/types/api'
import type { Tone } from '~/composables/useNodeState'

/**
 * Which panel this is, and how the pair is doing.
 *
 * Shared rather than local to the card, because the shell needs it too: a standby is read-only,
 * and an operator who does not know which of two identical panels they are looking at will try to
 * make a change on the wrong one. That belongs in the frame, not on one page.
 *
 * It fails quietly. A single panel has no pair, and so does a panel on an older build; neither is
 * a reason to put a red bar across the top of every page.
 */
export function usePanelState() {
  const api = useControlApi()

  const pair = useState<PanelPair | null>('awg-panel-pair', () => null)
  const loaded = useState<boolean>('awg-panel-loaded', () => false)

  async function refresh() {
    try {
      pair.value = await api.get<PanelPair>('/panel')
    } catch {
      // The fleet state already reports a panel that cannot be read at all; one message is enough.
    } finally {
      loaded.value = true
    }
  }

  const isStandby = computed(() => pair.value?.role === 'standby')

  /** True while this panel has nothing to show: enrolled or not, it has never replicated a fleet. */
  const blank = computed(() => isStandby.value && pair.value?.hasFleetState === false)

  /** A pair exists once there is more than one panel in it, or once this one is a replica. */
  const paired = computed(() => isStandby.value || (pair.value?.panels.length ?? 0) > 1)

  return { pair, loaded, isStandby, blank, paired, refresh }
}

/** One word for where a panel stands, in the order an operator needs it. */
export function describePanel(state: PanelState): { word: string, tone: Tone, icon: string } {
  switch (state) {
    case 'revoked':
      return { word: 'Revoked', tone: 'muted', icon: 'i-lucide-ban' }
    case 'active':
      return { word: 'In charge', tone: 'live', icon: 'i-lucide-crown' }
    case 'silent':
      return { word: 'Out of touch', tone: 'error', icon: 'i-lucide-unplug' }
    // Enrolled and talking, but holding no fleet: it cannot take over, and that is the one thing
    // a standby exists to be able to do.
    case 'never_synced':
      return { word: 'Nothing replicated', tone: 'warning', icon: 'i-lucide-circle-dashed' }
    default:
      return { word: 'Replicating', tone: 'standby', icon: 'i-lucide-refresh-cw' }
  }
}

import type { PanelFailoverAction, PanelPair, PanelState } from '~/types/api'
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

/**
 * What a standby concluded about taking over. One word per decision the policy can reach.
 */
const standbyVerdicts: Record<PanelFailoverAction, { word: string, tone: Tone, icon: string }> = {
  none: { word: 'Nothing to do', tone: 'success', icon: 'i-lucide-check' },
  wait: { word: 'Out of touch', tone: 'warning', icon: 'i-lucide-hourglass' },
  hold: { word: 'Holding after a switch', tone: 'warning', icon: 'i-lucide-pause' },
  recommend: { word: 'Take over by hand', tone: 'warning', icon: 'i-lucide-arrow-right-left' },
  refused: { word: 'Will not take over', tone: 'error', icon: 'i-lucide-octagon-alert' },
  promote: { word: 'Took over', tone: 'live', icon: 'i-lucide-crown' }
}

/**
 * What a round concluded, in words that fit the panel that concluded it.
 *
 * The action alone is not enough, because the panel in charge borrows the promotion policy's
 * vocabulary to report on something else entirely. Its rounds watch the pair rather than weigh a
 * promotion, so `refused` there does not mean "I will not take over" - it means this panel cannot
 * see its peer, or both claim to be in charge. Reading the standby's word out on an active panel
 * tells an operator the opposite of what happened.
 */
export function describeRound(
  action: PanelFailoverAction,
  role: 'active' | 'standby'
): { word: string, tone: Tone, icon: string } {
  // `promote` is the one verdict that means the same thing in both roles: the round that took
  // over reports itself as active, and "Took over" is exactly right.
  if (role === 'active' && action !== 'promote') {
    return action === 'none'
      ? { word: 'Pair healthy', tone: 'success', icon: 'i-lucide-check' }
      : { word: 'Pair degraded', tone: 'error', icon: 'i-lucide-unplug' }
  }

  return standbyVerdicts[action]
}

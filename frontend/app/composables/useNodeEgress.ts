import type { Node } from '~/types/api'

/**
 * Which egress names a peer can be sent through, read off the nodes that offer them.
 *
 * Derived rather than stored. A label is one node's name for one of its addresses, and what makes
 * it worth picking for a peer is that several nodes use the same name - then the peer keeps the
 * address it was given a reason for after failover moves it. So the list a peer chooses from is
 * the union, and how many nodes stand behind each name is the thing worth saying beside it.
 */
export function egressLabels(nodes: Node[]): string[] {
  const names = new Set<string>()
  for (const node of nodes) {
    for (const entry of node.egressPlan.labels) {
      names.add(entry.label)
    }
  }
  return [...names].sort((left, right) => left.localeCompare(right))
}

/**
 * The same names as the items of a select.
 *
 * Never an item for "no name": an empty string is what the underlying select reserves for its
 * own placeholder, and an item carrying one throws rather than rendering - which is how a peer
 * that had been given a name became impossible to put back on the node default. The absence is
 * the placeholder, and clearing it is a button.
 */
export function egressChoices(nodes: Node[]) {
  return egressLabels(nodes).map(label => ({ label, value: label }))
}

/**
 * Which of these nodes offer that name, and which do not. A peer on a node that does not leaves
 * through that node's default address - it keeps working, it just stops being special.
 */
export function egressCoverage(nodes: Node[], label?: string | null) {
  if (!label) {
    return { offering: [], missing: [] }
  }

  return {
    offering: nodes.filter(node => node.egressPlan.labels.some(entry => entry.label === label)).map(node => node.name),
    missing: nodes.filter(node => !node.egressPlan.labels.some(entry => entry.label === label)).map(node => node.name)
  }
}

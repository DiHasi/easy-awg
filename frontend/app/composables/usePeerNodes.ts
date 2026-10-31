import type { Client, ClientStats, Node } from '~/types/api'

/**
 * The node a peer is on, resolved for a page.
 *
 * Against every node the panel holds, not the serving ones: a peer whose newest handshake came
 * through a *revoked* node is the case an operator most needs to see, because revoking stops
 * configuration and not traffic - that node is still carrying them. Resolving against the serving
 * list is what made the Traffic dialog say "Connected through —" for exactly those peers.
 */
export function nodeOf(
  client: Client | null,
  stats: Record<string, ClientStats>,
  nodes: Node[]
): Node | null {
  const nodeId = client ? stats[client.id]?.nodeId : null
  return nodeId ? nodes.find(node => node.id === nodeId) ?? null : null
}

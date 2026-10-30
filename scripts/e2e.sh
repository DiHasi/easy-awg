#!/bin/sh
# The fleet, actually running: a panel, two nodes and a probe in containers on one host, driven
# through the admin API.
#
# This exists because the integration tests cannot reach the part that matters most. They host the
# real control plane, but the agent and the probe are stand-ins that sign requests and verify
# bundles - nothing in them brings an interface up, completes an AmneziaWG handshake, or carries a
# packet. Everything below the protocol was until now only ever exercised by hand:
#
#   - an agent applying a bundle with awg-quick and the bundled amneziawg-go, and reporting it;
#   - a probe completing a real handshake with each node and fetching a page through the tunnel;
#   - the panel telling a blocked node from a down one, from evidence a real probe produced;
#   - both kinds of block - a handshake that never completes, and one that completes and then
#     carries nothing, which is the pattern the traffic check exists to catch.
#
# It is deliberately not part of the pull-request CI: it builds both images, which pulls a Go
# toolchain and the AmneziaWG sources. .github/workflows/e2e.yml runs it nightly and on request.
#
# Needs: docker with compose, curl, jq, and /dev/net/tun on the host.
set -eu

cd "$(dirname "$0")/.."

COMPOSE="docker compose -f compose.e2e.yaml"
PANEL="http://127.0.0.1:18080"
JAR="$(mktemp)"
NODE_B_IP="172.28.0.12"
# A second address for node-a, added to its container below. The whole point of per-peer egress is
# a server that carries more than one, which no compose network hands out by itself.
NODE_A_SPARE="172.28.0.111"
TUNNEL_PORT="51820"
METRICS_TOKEN="e2e-scrape-token"
# A waiting step gives up after this many seconds. Probe rounds are 10s apart and a handshake is
# given 10s, so a verdict about a node can honestly take half a minute to turn up.
DEADLINE=150

say() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; exit 1; }

cleanup() {
    status=$?
    if [ "$status" -ne 0 ]; then
        say "logs (the run failed)"
        $COMPOSE logs --tail 120 || true
    fi

    say "tearing down"
    $COMPOSE down --volumes --remove-orphans || true
    rm -f "$JAR"
    exit "$status"
}
trap cleanup EXIT INT TERM

api() {
    method="$1"
    path="$2"
    shift 2
    curl -sS -b "$JAR" -c "$JAR" -X "$method" "$PANEL$path" -H 'Content-Type: application/json' "$@"
}

# One field of one node, by the name it was enrolled under.
node() { api GET /api/nodes | jq -r --arg name "$1" ".[] | select(.name == \$name) | $2"; }

# How many nodes satisfy a jq predicate, which is what most of the waiting below is about.
nodes_matching() { api GET /api/nodes | jq "[.[] | select($1)] | length"; }

panel_answers() { curl -sf "$PANEL/api/health" >/dev/null; }
two_agents_converged() { [ "$(nodes_matching '.interfaceUp and .inSync')" -ge 2 ]; }
two_nodes_probed() { [ "$(nodes_matching '.healthSource == "probes" and .probesReachable > 0')" -ge 2 ]; }
health_is() { [ "$(node "$1" .health)" = "$2" ]; }
node_a_reports_both_addresses() { [ "$(node node-a '.egressAddresses | length')" -ge 2 ]; }
# The rule the probe's own traffic has to pass through once node-a has a default of its own.
egress_default_carried_traffic() {
    $COMPOSE exec -T node-a iptables -t nat -L AWG_EGRESS -n -v -x \
        | awk '/10\.9\.0\.0\/24/ && $1 > 0 { found = 1 } END { exit found ? 0 : 1 }'
}
switch_recommended() { [ "$(api POST /api/failover/evaluate | jq -r .action)" = "recommend" ]; }

# Polls a predicate until it holds. The predicate is a function name, not a string of shell, so
# the quoting stays readable and a failure prints the fleet as the panel sees it.
wait_for() {
    what="$1"
    shift
    printf 'waiting for %s' "$what"

    waited=0
    while [ "$waited" -lt "$DEADLINE" ]; do
        if "$@" >/dev/null 2>&1; then
            printf ' ok (%ss)\n' "$waited"
            return 0
        fi

        printf '.'
        sleep 3
        waited=$((waited + 3))
    done

    printf '\n'
    api GET /api/nodes | jq -c '.[] | {name, health, healthReason, inSync, probesReachable}' || true
    fail "timed out waiting for $what"
}

say "building the images"
$COMPOSE build

say "starting the panel"
$COMPOSE up -d control
wait_for "the panel to answer" panel_answers

say "signing in and issuing tokens"
api POST /api/auth/login -d '{"username":"e2e","password":"e2e-password-not-a-secret"}' >/dev/null
NODE_A_TOKEN="$(api POST /api/nodes/tokens -d '{"name":"node-a","endpointHost":null,"egressInterface":null,"mtu":null}' | jq -r .token)"
# node-b is given an MTU of its own, so the per-node override travels through a real enrollment
# and has to end up in the config a real agent applies.
NODE_B_TOKEN="$(api POST /api/nodes/tokens -d '{"name":"node-b","endpointHost":null,"egressInterface":null,"mtu":1320}' | jq -r .token)"
PROBE_TOKEN="$(api POST /api/probes/tokens -d '{"name":"probe-home"}' | jq -r .token)"
[ -n "$NODE_A_TOKEN" ] && [ -n "$NODE_B_TOKEN" ] && [ -n "$PROBE_TOKEN" ] || fail "could not issue tokens"
export NODE_A_TOKEN NODE_B_TOKEN PROBE_TOKEN

say "enrolling two nodes and a probe"
$COMPOSE up -d node-a node-b probe
wait_for "both agents to bring their tunnel up and converge" two_agents_converged

say "the interface each agent actually brought up"
$COMPOSE exec -T node-a awg show || fail "node-a has no interface"
$COMPOSE exec -T node-b awg show || fail "node-b has no interface"

# The per-node override in the config a real agent wrote, not merely in the bundle it was sent.
$COMPOSE exec -T node-b sh -c 'grep -q "MTU = 1320" /etc/amnezia/amneziawg/awg0.conf' \
    || fail "node-b did not apply its own MTU"
$COMPOSE exec -T node-a sh -c 'grep -q "MTU = 1280" /etc/amnezia/amneziawg/awg0.conf' \
    || fail "node-a did not apply the fleet MTU"

say "a client config, rendered by the panel"
CLIENT_ID="$(api POST /api/clients -d '{"name":"e2e-laptop","obfuscation":null}' | jq -r .id)"
[ -n "$CLIENT_ID" ] || fail "could not create a client"
api GET "/api/clients/$CLIENT_ID/config" | grep -q "Endpoint = vpn.e2e.invalid:$TUNNEL_PORT" \
    || fail "the client config does not point at the fleet endpoint"

wait_for "every node to carry the new peer" two_agents_converged
$COMPOSE exec -T node-a awg show | grep -q peer || fail "node-a has no peers"

say "the probe: a real handshake with each node, then a request through the tunnel"
wait_for "the probe to get traffic through to both nodes" two_nodes_probed
health_is node-a healthy || fail "node-a is not healthy with a probe reaching it"
health_is node-b healthy || fail "node-b is not healthy with a probe reaching it"

say "pointing clients at node-a"
NODE_A_ID="$(node node-a .id)"
NODE_B_ID="$(node node-b .id)"
api POST "/api/nodes/$NODE_A_ID/activate" >/dev/null
[ "$(api GET /api/dns | jq -r .activeNodeId)" = "$NODE_A_ID" ] || fail "node-a did not become active"

# Per-peer egress: one node, two source addresses, and the panel deciding who leaves through
# which. Only the node knows which addresses it carries, so the panel has nothing to offer until
# the agent has reported them - which is why the address is added to the container first.
say "giving node-a a second address and letting it report both"
$COMPOSE exec -T node-a ip addr add "$NODE_A_SPARE/24" dev eth0
wait_for "node-a to report both of its addresses" node_a_reports_both_addresses

say "naming them, and sending one peer through the one that is not the default"
CLIENT_ADDRESS="$(api GET /api/clients | jq -r --arg id "$CLIENT_ID" '.[] | select(.id == $id) | .address')"
[ -n "$CLIENT_ADDRESS" ] || fail "could not read the client tunnel address"

EGRESS_REVISION="$(api GET /api/fleet | jq .revision)"
api PUT "/api/nodes/$NODE_A_ID/egress" \
    -d "{\"defaultAddress\":\"$NODE_A_SPARE\",\"labels\":[{\"address\":\"172.28.0.11\",\"label\":\"home\"}]}" >/dev/null
api PUT "/api/clients/$CLIENT_ID/egress" -d '{"label":"home"}' >/dev/null

# Both halves travel in the bundle, so the node has to be told: an unchanged revision is answered
# with a 304 and this is exactly the node it is about.
[ "$(api GET /api/fleet | jq .revision)" -gt "$EGRESS_REVISION" ] || fail "an egress change did not move the revision"
wait_for "node-a to apply it" two_agents_converged

say "the rules a real agent installed for it"
CHAIN="$($COMPOSE exec -T node-a iptables -t nat -S AWG_EGRESS)"
echo "$CHAIN"
echo "$CHAIN" | grep -q -- "-s $CLIENT_ADDRESS/32 -j SNAT --to-source 172.28.0.11" \
    || fail "the pinned peer has no rule of its own"
echo "$CHAIN" | grep -q -- "-s 10.9.0.0/24 -j SNAT --to-source $NODE_A_SPARE" \
    || fail "the node default was not installed"
# The pin has to be seen first: SNAT terminates, so a default that covers the whole subnet above it
# would mean no peer ever reaches a rule of its own.
[ "$(echo "$CHAIN" | grep -n -- "-s $CLIENT_ADDRESS/32" | cut -d: -f1)" \
    -lt "$(echo "$CHAIN" | grep -n -- "-s 10.9.0.0/24" | cut -d: -f1)" ] \
    || fail "the node default is above the per-peer rule"

# And the jump has to be above the blanket MASQUERADE, for the same reason one chain up.
POSTROUTING="$($COMPOSE exec -T node-a iptables -t nat -S POSTROUTING)"
[ "$(echo "$POSTROUTING" | grep -n -- "-j AWG_EGRESS" | cut -d: -f1)" \
    -lt "$(echo "$POSTROUTING" | grep -n -- "-j MASQUERADE" | cut -d: -f1)" ] \
    || fail "the egress chain is below the MASQUERADE"

# The data path, not just the rule text: the probe keeps fetching through the tunnel, and that
# traffic is unpinned, so it is the default rule it has to pass through.
wait_for "traffic through the tunnel to pass through the egress chain" egress_default_carried_traffic
health_is node-a healthy || fail "node-a stopped serving traffic once it had a default egress address"

# The block that reads as perfectly healthy from the inside, and the reason the traffic check
# exists at all: the handshake completes and nothing after it does. Arranged by dropping
# forwarding on the node itself, which is also exactly how a node with broken egress looks.
say "breaking node-a after the handshake (the DPI pattern)"
$COMPOSE exec -T node-a iptables -I FORWARD 1 -i awg0 -j DROP

wait_for "the panel to call node-a blocked" health_is node-a blocked
node node-a .healthReason | grep -qi "handshake" || fail "the reason does not say the handshake completed"

say "what the panel decides about it"
wait_for "the panel to recommend a switch" switch_recommended
DECISION="$(api POST /api/failover/evaluate)"
echo "$DECISION" | jq -c '{action, message, targetNodeId}'
[ "$(echo "$DECISION" | jq -r .targetNodeId)" = "$NODE_B_ID" ] || fail "the recommendation should name node-b"

say "the availability history and the scrape endpoint agree with the cards"
api GET "/api/nodes/availability?window=24h" \
    | jq -e --arg id "$NODE_A_ID" '.nodes[] | select(.nodeId == $id) | select(.blocked > 0)' >/dev/null \
    || fail "the history recorded no blocked samples for node-a"

curl -sS -H "Authorization: Bearer $METRICS_TOKEN" "$PANEL/api/metrics" \
    | grep -q "awg_node_health{node=\"node-a\",node_id=\"$NODE_A_ID\",state=\"blocked\"} 1" \
    || fail "the scrape endpoint does not report node-a as blocked"

say "letting node-a recover"
$COMPOSE exec -T node-a iptables -D FORWARD -i awg0 -j DROP
wait_for "node-a to recover" health_is node-a healthy

# The other kind of block: the address itself is unreachable from where the probe stands, so no
# handshake ever completes. The node is perfectly fine and says so.
say "blocking node-b at the port, from the probe's own network"
$COMPOSE exec -T probe iptables -I OUTPUT 1 -d "$NODE_B_IP" -p udp --dport "$TUNNEL_PORT" -j DROP

wait_for "the panel to call node-b blocked" health_is node-b blocked
node node-b .healthReason | grep -qi "address or the port" \
    || fail "the reason does not say the address or the port is blocked"
[ "$(node node-b .interfaceUp)" = "true" ] || fail "node-b should still report its own tunnel up"

$COMPOSE exec -T probe iptables -D OUTPUT -d "$NODE_B_IP" -p udp --dport "$TUNNEL_PORT" -j DROP
wait_for "node-b to recover" health_is node-b healthy

say "switching the fleet to node-b"
REVISION="$(api GET /api/fleet | jq .revision)"
api POST "/api/nodes/$NODE_B_ID/activate" >/dev/null
[ "$(api GET /api/dns | jq -r .activeNodeId)" = "$NODE_B_ID" ] || fail "node-b did not become active"
# Switching repoints a record. Nothing is reissued and no node re-applies anything, which is the
# whole point of a shared fleet identity - so the revision must not have moved.
[ "$(api GET /api/fleet | jq .revision)" = "$REVISION" ] || fail "a switch moved the fleet revision"

say "all good"

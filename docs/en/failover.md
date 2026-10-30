[← Documentation](../README.md) · **English** · [Русский](../ru/failover.md)

# Failover and probes

## Switching which node clients use

Every client config names one host and pins the fleet's server public key. Failover is therefore
nothing more than making that host resolve to a different node: same key, same subnet, same
obfuscation profile, different server. Nothing is reissued, no node re-applies anything, and the
fleet revision deliberately does not move.

What a switch does change is which addresses are on offer. A peer sent through one of a node's
[egress addresses](panel.md#nodes) asked for it by name, so on the new node it leaves through the
address called by that name - and where the new node has none, through that node's default. The
peer keeps working either way; its row in the peer list says when the name is doing nothing today.

In the panel: **Nodes → Make active** on the node you want clients on. Each node shows the public
address its own agent discovered, which is what the record is pointed at, and the card at the top
of the page shows the record, its TTL, and whether the panel's own resolver already agrees.

With `AWG_CLOUDFLARE_API_TOKEN` and `AWG_CLOUDFLARE_ZONE_ID` set, the panel edits the record
itself - an A or AAAA record, never proxied, since the orange cloud carries HTTP only and would
swallow the tunnel. The token needs `Zone:Read` and `Zone:DNS:Edit` on that zone and nothing
else. If the API refuses, the switch is refused with it: the panel never claims a node is active
while the record still points elsewhere.

Before every edit the panel checks that the record name actually belongs to the configured zone,
and that it answers through exactly one address record. A name with several A records, both an A
and an AAAA record, or a CNAME is refused with a reason rather than having "the first" record
moved while clients keep resolving the others. A name with no address record at all is not an
error: the panel creates it.

Without a token the button still works - it records which node is active and shows the record to
set - and the resolver check is what confirms you made the change.

Clients move over as resolvers expire the answer they were handed, so the record's TTL is the real
speed limit. A node does not need to be reachable for this: switching away from a dead node is
exactly the case it is for.

If a node reports no address at all, its agent could not reach any echo service. Set `AWG_PUBLIC_IP`
on that node to state it directly.

## Automatic failover and probes

A blocked node looks perfectly healthy from the inside: its agent reports in, the interface is up,
and clients simply cannot reach it. So the panel judges each node from two kinds of evidence:

- **the agent's own reports** say whether the node runs - tunnel up, reporting on time;
- **probes** say whether a client can reach it. A probe is the node image run with
  `AWG_ROLE=probe` on a host where your clients are - ideally inside the network that does the
  blocking. Every minute it brings up a throwaway interface against each node, with a real client
  key and the fleet's wire format, waits for the AmneziaWG handshake, and then fetches a page
  through the tunnel. Only an answer counts: the common block lets the first handshake through and
  drops everything after it, so a handshake alone would read a blocked node as healthy. A TCP
  connect or a ping would sail through exactly the blocks that matter.

Together they give each node one of these states, shown on its card:

| State | Meaning |
| --- | --- |
| healthy | A probe got traffic through the tunnel - or, with no probe watching, the agent reports the tunnel up. A completed handshake on its own is not enough, for the reason above. |
| blocked | The agent reports the tunnel up, and no probe gets traffic through. The reason says whether the handshake itself failed (address or port blocked) or completed with nothing after it (DPI, or the node's egress is broken). |
| down | The agent reports the tunnel down, or neither the agent nor any probe reaches it. |
| silent | No status report for `AWG_NODE_STALE_SECONDS` and no probe watching it. |
| unknown | Enrolled, and has not reported yet. The panel has not been told anything, which is not the same as a node being unwell. |
| revoked | Cut off on purpose: it receives no further configuration, so the panel records nothing about it and failover will not pick it. |

Add a probe under **Nodes → Probes → Add probe**; the panel hands you a `docker run` line. A probe
never receives a bundle and never holds the fleet private key: it is a client of the fleet with a
key of its own, hidden from the client list, and a probe token cannot enroll a node or the other
way round. If the probe cannot even run its check, that is reported as a probe error and never
counts against the node, so a broken probe can never move traffic.

If you prefer compose, use `.env.probe.example` with `compose.probe.yaml`.

**Automatic failover** is off until you arm it under **Nodes → Automatic failover**, and it can
only be armed with a DNS provider configured - arming it runs the same zone and record checks as a
switch, so a problem shows up while you are looking. Once armed, the panel moves the record when
the active node has been blocked, down or silent for `AWG_FAILOVER_GRACE_SECONDS`. It picks a node
that is healthy right now, has an address, runs the current bundle schema and is not excluded -
preferring one that has applied the latest revision, then one probes have reached, then the lowest
priority number (set per node under **Failover settings**), and finally the one enrolled first, so
that two otherwise equal nodes are not picked between at random. A node still below the fleet's
identity revision is refused outright rather than merely ranked low: it answers with the previous
key pair, so no config issued since the rotation can handshake with it. It then waits
`AWG_FAILOVER_COOLDOWN_MINUTES` before it will switch again, and it never switches back on its own.
The switch is the very same call as the **Make active** button, so it does not touch the fleet
revision either.

With failover left manual the panel still watches: it tells you when a node is blocked or down, and
when a switch is due, and leaves the button to you.

Notifications go to a webhook (`AWG_NOTIFY_WEBHOOK_URL`, a JSON POST) and/or Telegram
(`AWG_NOTIFY_TELEGRAM_BOT_TOKEN` and `AWG_NOTIFY_TELEGRAM_CHAT_ID`): nodes becoming blocked, down,
silent or recovering, switches, and a failover that is stuck or refused. **Test** sends one.

## Availability history

The node cards answer what is wrong right now. **Nodes → Availability** answers how often it has
been: one row per node, hour by hour, over the same windows the Traffic page offers.

Each bucket is coloured by the worst state that occurred in it rather than by the most common one,
because an hour that was blocked for ten minutes is an hour worth seeing, and averaging it away is
how a flapping node comes to look fine. An hour the panel recorded nothing in - it was restarted,
or had only just been installed - is drawn as a gap and counts towards nothing: "no evidence" and
"not healthy" are different answers, and a share is only ever taken over the checks that ran.

The minute after a [rotation](security.md) is left out for the same reason. For up to one probe
round afterwards every probe is still speaking the previous wire format, so no handshake completes
anywhere and the fleet would otherwise be recorded as blocked while nothing was wrong with any of
it. The cards still show what the probes see during that minute - the grace period is what keeps
anything from being done about it - but the history keeps no block the panel caused itself, and
records nothing in its place: a window filtered down to the checks that went well would claim an
uptime the evidence does not support.

Below the rows, the same window **per probe**. This is the only place the panel can say *where* a
node is blocked: a node no probe reaches and a node only the office probe reaches are different
problems with different fixes. The handshake count beside it separates the two kinds of block -
handshakes that complete with no traffic behind them are DPI or broken egress, handshakes that
never complete are the address or the port.

### Clearing it

When the record is wrong rather than merely old, **Clear** in the card's header drops all of it, and
the menu on a node's row drops that node's alone. A rotation no longer leaves a mark, but other
reasons outlive it: a probe whose own uplink was down records the nodes it could not reach as
unreachable, a node rebuilt under its old row carries the blocks of the host it no longer is, and a
rotation made before this version left a fleet-wide notch that is still in the record. What a
bucket holds is a count, not the checks behind it, so there is nothing to correct in place - only
the claim to withdraw.

It touches the panel's record and nothing else: no node hears about it, the fleet revision does not
move, every client keeps its config, and the states on the cards are judged from current evidence
either way. Recording starts again on the next check. On a pair it clears the panel you are on - a
standby replicates the history forwards from a cursor and keeps what it already holds, so if you
promote it later, clear it again there.

It is recorded from the failover rounds, so the numbers are the same verdicts the cards show, from
the same evaluator. `AWG_AVAILABILITY_RETENTION_DAYS` decides how long it is kept, and `0` records
none - the states on the cards are judged live and keep working either way. It is a separate knob
from the traffic retention on purpose: that one is a decision about holding a record of people, and
it has nothing to say about whether the panel may remember which of your own nodes was blocked last
week.


---

[← Using the panel](panel.md) · [Documentation](../README.md) · [A second panel →](panel-pair.md)

[← Documentation](../README.md) · **English** · [Русский](../ru/panel.md)

# Using the panel

Five pages, in the order they appear in the sidebar: **Overview**, **Nodes**, **Traffic**,
**Settings** and **Log**.

## Overview

Two cards: where client traffic goes right now, and the peers themselves.

The **Fleet** card draws the record, the active node and the line between them, so the path
traffic actually takes is the thing you see first. A node's own actions — making it active, its
settings, revoking it — are on the node in the graph, the same actions the Nodes page offers.

**Peers** is the list of configs. Create, rename, enable, disable and delete them; download a
config, show a QR code, or create a 24-hour share link for someone without an account. Live
traffic and handshake data is aggregated across every node, per peer, and the counter can be zeroed
from the panel: the kernel will not reset a peer counter without tearing the peer down, so the
panel remembers where the counter stood and reports the difference.

A share link shows only the QR code, the config and a link to the AmneziaWG build for whatever
device opened it — never the name the peer is filed under here.

A peer is marked when the config it holds was issued against an identity the fleet has since
rotated, so after a rotation the list itself is the checklist of who still has to be handed a new
one; the mark clears as soon as that config is rendered again.

**Groups** live in the same list. One person usually holds several configs, so peers can be filed
into groups: a group is a person, the peers in it are their devices. Drag a peer by its handle to
move it between groups or to reorder it, drag a group header to reorder the groups, or use *Move
to* in the row's menu where a pointer is awkward. The arrangement is stored in the panel's
database, not in the browser, so the same list comes up on every device you sign in from. It is
pure bookkeeping: no node is told about a group, no config is reissued, and deleting a group only
removes the label — the peers in it move to the ungrouped list and keep working.

## Nodes

The DNS record at the top, then a card per node: the status of each agent, its public address,
whether it has picked up the current revision, which one clients are currently sent to, and
revocation. Revoking a node cuts off its configuration on its very next request. **Enroll node**
hands you a one-line install command.

*Node settings* on a node's own menu holds what that node runs and the rest of the fleet does not —
its own label, its endpoint host, its egress interface and its own tunnel MTU. The same fields are
offered when a node is enrolled, so a server that needs a narrower MTU has it in the first
configuration it ever applies. Of the four, only the egress interface and the MTU travel in the
bundle; the label and the endpoint host reissue nothing.

Below the cards: how each node has stood over time and what each probe saw of it, automatic
failover, the panel pair, and the probes.

## Traffic

What the fleet moved over the last day, week or month, and who moved it. The counters on the peer
list are lifetime totals; this is when that traffic happened, which is the only form in which a
question about a person has an answer: who is costing the most this month, who has stopped using
the config they were given, which evening the active node was saturated.

Because a group is a person, the ranking is people first and their devices inside them — open a
line to see the devices, open a device to see its own history. The numbers are differences between
two status reports, credited to the hour the later one arrived in, so a peer's traffic appears once
its node has reported twice. Hourly rows are kept for `AWG_USAGE_RETENTION_DAYS` and deleted with
the peer; set it to `0` and the panel records nothing at all, while the live counters — which come
from the nodes, not from here — keep working.

## Settings

The fleet itself: the shared identity, the tunnel MTU, the obfuscation profile, the legacy import,
and *Rotate identity* for a node you have to assume was read. It says how many peers are still
holding a config from an older generation, which after a rotation is the list of people yet to be
found.

## Log

An audit trail of who changed what and which nodes fetched configuration. It is each panel's own
account of what was done on it, so a standby's log is its own and not a copy of the other one's.

---

[← Getting started](getting-started.md) · [Documentation](../README.md) · [Failover and probes →](failover.md)

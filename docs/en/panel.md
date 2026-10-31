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

Each row names the node that peer is on, beside its address, taken from the newest handshake any
node in the fleet reported for it. A peer still being carried by a revoked node says so — the one
case where the panel has cut a node off and people are on it anyway.

A share link shows only the QR code, the config and a link to the AmneziaWG build for whatever
device opened it — never the name the peer is filed under here.

A peer is marked when the config it holds was issued against an identity the fleet has since
rotated, so after a rotation the list itself is the checklist of who still has to be handed a new
one; the mark clears as soon as that config is rendered again.

**Limits** on a peer's menu is when its config stops working on its own: an expiry, an allowance
in gigabytes, or both. An allowance is counted either in total - a trial, counted from the moment
you set it and never reset by the panel - or per calendar month in UTC, which puts the peer back
when the next month starts. Reaching either disables the peer and nothing else: its keys, its
address and its place in the list are untouched, so extending the expiry or starting the allowance
again puts exactly that config back in service.

The panel reads the limits once a minute, so a peer can carry traffic for up to a minute after its
allowance is gone. That is deliberate - a quota is a billing instrument and the nodes enforce
nothing - and it is the same order of imprecision as the twenty seconds between a node's reports.
What is counted is received and sent together, as the difference between two of those reports, kept
on the peer rather than read back out of the traffic history: `AWG_USAGE_RETENTION_DAYS` is a
decision about holding a record of what people did and has nothing to say about whose allowance
should start again, so allowances are counted even with the history switched off, and zeroing a
peer's live counters does not hand its allowance back either.

A peer the panel switched off says which limit did it, on its row and in the dialog, because one
of them is fixed by a date and the other by a number. Enabling such a peer by hand is refused
rather than undone by the next sweep a minute later. A peer **you** disabled carries no reason, and
a monthly allowance renewing never puts it back - that was your decision, not a limit.

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

*Egress addresses* on the same menu is the other half of what one node runs: which of its own
addresses its traffic leaves through. The reason this exists is a provider handing you a second
address on one server — one of them has been filed by the services behind it as belonging to a
region it is not in, or into somebody's shadow ban, and the two halves of your fleet want opposite
things from it. The rows are what the agent reported, because only the node knows which addresses
it carries, and each row says what an echo service answered when a request left with that address
as its source. Name an address and a peer can ask for it. Pick one as the default and everybody
else leaves through it, rather than through whatever the kernel picks out of the order the
addresses happen to sit in on the interface.

A peer asks by name and not by address — *Egress address* on its row in the peer list — because an
address belongs to one node. After a switch the peer leaves through the address the new node calls
by the same name, or through that node's default where it has none, and its row says so. *Use
node default* in the same dialog puts it back, and *Clear* beside a node's default address hands
the choice back to the kernel. Saving
either half advances the fleet revision, so every node re-applies its configuration: no interface
is taken down, no config is reissued, and a connection already open keeps the address it was made
with.

Each card also says what that node is carrying — how many peers handshaked through it just now,
and how many were last seen there and have gone quiet — with *Peers on this node* in its menu
listing both by name. That is the number to read before switching a node off; what it means, and
what it cannot tell you, is under [draining a node](failover.md#draining-a-node).

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

**Operators** on the same page is who can sign in. Change your own password - the current one is
required - add a second operator, reset somebody else's password, or remove an account, which ends
the session it already had. The account you are signed in with cannot be removed, and that is also
what stops the panel from ending up with no operator at all. The rules, and what a password change
does not do, are under [operator accounts](security.md#operator-accounts).

## Log

An audit trail of who changed what and which nodes fetched configuration. It is each panel's own
account of what was done on it, so a standby's log is its own and not a copy of the other one's.

---

[← Getting started](getting-started.md) · [Documentation](../README.md) · [Failover and probes →](failover.md)

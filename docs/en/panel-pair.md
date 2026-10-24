[← Documentation](../README.md) · **English** · [Русский](../ru/panel-pair.md)

# A second panel

[Failover and probes](failover.md) protects the fleet from losing a node. This protects it from
losing the panel.

A **standby panel** is the same image with a database of its own, pointed at the panel you already
run. It replicates everything the fleet needs - the fleet identity, the bundle signing key, every
client with its private key, the groups and the arrangement, each node's own settings, the probes,
the admin accounts, the share links, and both histories - and it refuses every change. When the
panel in charge is gone, one click makes the standby the one in charge, and **no client config is
reissued**: a config pins the fleet public key, which both panels hold.

```bash
# On the panel you already run, before enrolling anything:
#   AWG_PANEL_URL=https://panel.example.com     how the standby reaches it
#   AWG_PANEL_PUBLIC_IP=203.0.113.1             what the panel record points at
#   AWG_PANEL_DNS_RECORD_NAME=panel.example.com optional: the record agents follow
```

Then **Nodes → Panel pair → Enroll standby**. The panel hands back three variables and a
one-time token, good for 30 minutes. Put them in the second machine's environment beside the same
`AWG_ENDPOINT_HOST`, its own `AWG_PANEL_URL` and `AWG_PANEL_PUBLIC_IP`, and start it:

```bash
AWG_PANEL_ROLE=standby
AWG_PANEL_PEER_URL=https://panel.example.com
AWG_PANEL_ENROLLMENT_TOKEN=<from the panel>
AWG_PANEL_URL=https://standby.example.com
AWG_PANEL_PUBLIC_IP=203.0.113.9
```

It enrolls itself, pulls the fleet, and from then on pulls every 30 seconds. Both panels show the
pair, the term, and how far behind the replica is. A standby puts a band across the top of every
page saying it is read-only, so you cannot mistake one for the other.

**Enrolling a standby hands that machine the whole control plane** - the fleet private key, the
bundle signing key, every client private key and every admin password hash. That is what it takes
to keep the fleet converging when the first panel is gone. Put it somewhere you trust as much as
the first one.

## Switching panels

Press **Take over** on the standby. It starts accepting changes at the next term, points
`AWG_PANEL_DNS_RECORD_NAME` at itself if one is configured, and the other panel steps down the
next time the two can see each other.

The **term** is what makes this safe to do under pressure. It is a number that only ever goes up,
one per switch, and the pair settles who is in charge by comparing it - no quorum and nothing to
arbitrate. A panel that comes back up still believing it is in charge asks its peer once, sees a
higher term, and steps down to standby on the spot. Until it manages that one call it is still
accepting changes nobody will see, which is why the card tells you to check it rather than
pretending the switch was instantaneous.

Agents follow `AWG_PANEL_DNS_RECORD_NAME`. If you have not configured one, they keep reaching the
old panel until you repoint them - and that costs you the fleet's *management*, never its traffic:
nodes are fail-static, so a node that cannot reach a panel re-applies its cached bundle and keeps
serving indefinitely. The panel says so after a switch rather than claiming more than it did.

## Letting the standby take over by itself

Off until you arm it, under the same switch on the card, and it is more reluctant than node
failover for a reason worth knowing before you arm it.

A node has probes in other networks to vouch for it: when three probes cannot reach a node, the
node is the problem. A panel has no such witness. When a standby cannot reach the panel in charge,
it has learned exactly one thing - that it cannot reach it - and it cannot tell that apart from
its own uplink having broken. The nearest thing to evidence is whether it can still reach anything
at all, which is what `AWG_PANEL_WITNESS_URLS` is for, and arming is refused without it.

So an armed standby takes over only when all of this holds:

- it has failed to pull for `AWG_PANEL_FAILOVER_GRACE_SECONDS` (default 300 - ten missed polls),
- it already holds the fleet, because a panel promoted with nothing would publish an empty fleet
  and every node would dutifully drop every peer it serves,
- it can still reach one of the witness URLs,
- it is not within `AWG_PANEL_FAILOVER_COOLDOWN_MINUTES` of its own last role change,
- and it has not been **held**, which is a switch on the standby itself and the one control that
  works without the other panel being reachable.

Even then, if the two panels are only cut off from *each other*, both end up accepting changes
until they can see each other again - at which point the lower term steps down and its changes are
gone. Manual promotion has none of that risk, which is why it is the default.

---

[← Failover and probes](failover.md) · [Documentation](../README.md) · [Obfuscation and MTU →](obfuscation.md)

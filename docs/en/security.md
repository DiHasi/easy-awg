[← Documentation](../README.md) · **English** · [Русский](../ru/security.md)

# Security model

- The admin API requires an authenticated session. Only `/api/health`, `/api/auth/*`,
  `/api/shares/*` and `/api/metrics` are anonymous, and the last of those is off until
  `AWG_METRICS_TOKEN` is set and checks that token itself, in fixed time — it is the one route
  that authenticates with something other than the admin cookie.
- Agents authenticate by **signing each request** with a key generated on the node itself. The
  private key never crosses the wire, and unlike mTLS this survives a TLS-terminating proxy or CDN
  in front of the panel.
- Configuration bundles are signed by the control plane and verified against a key the agent
  pinned at enrollment. Signatures alone are not enough — a correctly signed bundle stays valid
  forever — so bundles also carry a monotonic revision, a node binding and an expiry, and an agent
  refuses anything older than what it has already applied.
- Node revocation is a flag checked on every request, so it takes effect immediately. There is no
  revocation list to distribute.
- Enrollment and share tokens are stored only as hashes.
- A **standby panel** authenticates the same way and verifies what it is handed against the same
  pinned signing key - but what it receives is not comparable to what a node or a probe receives.
  A node gets public keys and addresses; a probe gets one client config. A standby gets the fleet
  private key, the bundle signing key pair, every client private key and every admin password
  hash, because that is what it takes to keep the fleet converging when the first panel is gone.
  Enrolling one is handing over the panel, not a share of it: treat that machine exactly as you
  treat the first, and `POST /api/fleet/rotate` if it is ever read.

Assume that a hosting provider can read a node's disk. Treat any seized node as a compromise of
the whole fleet, since every node carries the shared identity. When that happens, rotate.

## Rotating the fleet identity

**Settings → Fleet identity → Rotate identity**, or `POST /api/fleet/rotate`. This is the answer to a
node you have to assume was read, and it is the only thing in the panel that replaces the shared
identity. In one step it draws a new server key pair, a new preshared key for every peer - probes
included, enabled or not - and a fresh fleet-wide wire format (`S1`-`S4`, `H1`-`H4` and the header
protection key, which are in the config on that node's disk and are what makes the fleet's traffic
recognizable as this fleet's), moves the fleet to the next generation and bumps the revision once.

What it costs and what it does not:

- **Nodes need nothing.** They pick the whole change up on their next poll, and `awg syncconf`
  carries keys, so no interface is restarted and no server has to be visited.
- **Every client config in the field stops working at once.** They pin the old server public key
  and carry the old preshared key. There is no overlap window: one identity answers at a time.
- **Every share link stops resolving.** A link names the generation it was made for. The token
  went to its recipient over whatever channel was at hand, and after a rotation it must not be
  what hands out the replacement.
- **The bundle signing key is left alone,** so no node has to be re-enrolled. Agents pin it at
  enrollment and would refuse bundles signed by anything else.
- **Client key pairs are kept unless you ask.** A node never held one, so a read node does not
  expose them. Turn on *Also reissue every client key pair* if the panel's own database has to be
  assumed read too; every peer's public key then changes as well, and their live traffic counters
  start again from zero.
- **It does not take the compromised server off the air.** Nodes are fail-static, so revoking one
  only stops new bundles reaching it: it re-applies its cached bundle indefinitely and keeps
  serving the configs that were issued before the rotation. *Revoke, then rotate* is not
  containment — power the host down, or have the provider destroy it. The panel says so in the
  rotation result, naming the revoked nodes it applies to.
- **A node that has not applied the rotation yet is not a failover target.** It answers with the
  previous key pair while reporting itself healthy, so no current config works there. The panel
  marks it *Previous identity*, keeps automatic failover off it, and refuses *Make active* with
  `node_previous_identity` until it converges — one poll interval, normally under 20 seconds.
  Because probes only re-render their own config on their next round, keep
  `AWG_FAILOVER_GRACE_SECONDS` above `AWG_PROBE_INTERVAL_SECONDS`: for up to one round after a
  rotation no probe can complete a handshake anywhere, and the grace period is what stops that
  reading as a fleet-wide outage.

Afterwards the work is handing the new configs out, so the panel tracks it: a peer is marked
`generation 3` on the **Clients** list until its config has been rendered again, which downloading
it, showing its QR code or opening a fresh share link all do. The Fleet page counts how many are
still outstanding. A peer no config was ever rendered for is not marked - nothing of its is in
anybody's hands - and the mark is not a claim that anybody installed anything; it says the panel
produced the current config for that peer, which is the most it can honestly know. A rotation is written to the event log and announced through `AWG_NOTIFY_*` if a
channel is configured - if the person pressing it is not the person reading that channel, it is
the most important thing the fleet could tell them.

---

[← Obfuscation and MTU](obfuscation.md) · [Documentation](../README.md) · [Operations →](operations.md)

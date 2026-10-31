[← Documentation](../README.md) · **English** · [Русский](../ru/operations.md)

# Operations

## Metrics

Set `AWG_METRICS_TOKEN` and the panel exports Prometheus metrics at `/api/metrics`:

```
awg_node_health{node="helsinki-1",node_id="...",state="blocked"} 1
awg_node_revision_lag{node="helsinki-1",node_id="..."} 2
awg_node_report_timestamp_seconds{node="helsinki-1",node_id="..."} 1771000000
awg_probe_reachable{probe="moscow-home",probe_id="...",node="helsinki-1",node_id="..."} 0
awg_probe_handshake{probe="moscow-home",probe_id="...",node="helsinki-1",node_id="..."} 1
awg_node_peers_online{node="helsinki-1",node_id="..."} 3
awg_node_peers_idle{node="helsinki-1",node_id="..."} 2
```

The last two are what a drain alerts on: `awg_node_peers_online` is peers handshaking through that
node right now, `awg_node_peers_idle` is peers whose last known node it is and which are quiet.
`awg_node_peers`, beside them, is a different number - every peer the node is configured with,
which in a fleet is nearly the whole client list. None of the three carries a label naming a peer.

```yaml
# prometheus.yml
scrape_configs:
  - job_name: awg
    metrics_path: /api/metrics
    authorization:
      credentials: your-token
    static_configs:
      - targets: ['panel.example.com']
```

It exists because the panel is the only place that knows some of these facts - that a node is
blocked rather than down, that it is three revisions behind, that no probe has reported in ten
minutes - and finding that out should not require keeping a browser tab open. The states come from
the same evaluator the pages use, so a dashboard and the panel cannot disagree about whether a node
is healthy.

Three things worth knowing:

- **It is off until the token is set.** This is the one route that answers without an admin
  session; it checks the token itself, in fixed time. The health and whereabouts of a fleet of VPN
  nodes is not something to start publishing because a panel was upgraded.
- **Nothing about a person is exported.** No client names, no public keys, no per-peer series. A
  scrape is federated, backed up and shared far more casually than the panel's database, and a peer
  label would put "who" into all of it. Traffic is exported per node, which is the operational
  question; the Traffic page keeps the other one. Node addresses are left out for the same reason -
  a fleet's addresses are what a blocker wants.
- **Node counters are raw.** A counter reset in the panel is bookkeeping about a person, and a
  scraper is the one reader that handles a counter going backwards on its own, so subtracting there
  would only hide a node reboot from it.

## Backing the panel up

**Settings → Export and restore → Download export**, or `GET /api/fleet/export`. The file is what
this deployment cannot rebuild: the fleet key pair, the bundle signing key and every client's own
key pair live in one SQLite file, and losing it stops every config in every person's hands with
nothing to hand out in their place. Everything else can be rebuilt - a node from the install
one-liner, a probe likewise.

What is in it is what the panel publishes: the identity, the peers with their keys, the groups and
their arrangement, each node's own settings, the probes, the operator accounts with their password
hashes, the live enrollment tokens and share links, and the counter baselines. What is not is what
the panel merely observed - the agents' reports, the traffic history, the availability history and
the event log. It is the same split [a standby](panel-pair.md) replicates, deliberately: a restored
panel knows what to publish and has observed nothing yet, which is the state any panel is in when
it first comes up.

**It is the database, in plaintext.** The fleet private key and every client private key are in
the file, and nothing is encrypted. That is a choice rather than an omission: a passphrase would
make the one irrecoverable thing in the deployment recoverable only while somebody still remembers
a second secret, which is a bad trade for a file whose job is to be readable years later in a bad
week. Keep it where you would keep this panel's own disk. A download is written to the event log,
naming who took it, and the read-only [demo account](demo.md) is refused it outright rather than
handed a masked one - a key pair is not a name, so there is nothing to mask it to.

A standby covers losing the host; this covers the mistake a standby follows. A rotation nobody
meant to run, an import with *Replace existing clients* left on, a deletion - all of those reach
the replica within one poll. An export taken **on** a standby is only as fresh as its last
replication, which is why the file says which half of the pair it came from.

## Restoring the panel

**Settings → Export and restore → Restore from an export**, or `POST /api/fleet/restore` with the
file as the body and `?replace=true`. It is wholesale: nothing is merged, so a peer created after
the export was taken is not there afterwards, and the restore is refused on a panel that already
has peers or nodes until the replacement is confirmed. A standby refuses it entirely, like every
other write.

Normally it goes onto a fresh panel: bring up the container with the same `AWG_ENDPOINT_HOST`,
sign in with the bootstrap account, restore, and the fleet is as it was. Afterwards:

- **The agents reconnect on their next poll** and nothing is reissued. Each node keeps its own key
  pair, the export carries its row, so it is enrolled again as far as the panel is concerned.
  Until then it serves traffic from its cached bundle, which is the fail-static design doing its
  job. An agent whose node row is *not* in the export is not enrolled here and will be turned
  away: re-enroll it.
- **Sign in with the passwords as they were when the export was taken.** Operator accounts come
  out of the file, not out of `AWG_ADMIN_USER`, which only ever creates an account on an empty
  database.
- **This panel takes charge at a term above the one in the file.** The export names whichever panel
  was in charge when it was taken, which is usually the one being replaced; if that panel is in
  fact still running, it is told it has been superseded on its next hello and steps down. Last
  promotion wins, as everywhere else in the pair.
- **The histories start again from here.** The traffic and availability histories are not in an
  export, and the event log never is: it is each panel's own account of what was done on it.

## Recovering a node

Revoking or deleting a node in the panel cuts off its configuration, but it does **not** stop the
tunnel: the agent keeps serving traffic from its cached bundle. That is the fail-static design
working, not a bug.

To bring such a server back, issue a fresh enrollment token and re-run the installer. If the old
node still exists in the panel, delete it first — the agent keeps one key pair for its lifetime,
and the panel refuses to register the same server twice.

If the node's own identity is what you want to discard — a lost or suspect server — remove its
state and let it enroll from scratch:

```bash
docker rm -f awg-node && rm -rf /etc/awg-node/*
```

## Migrating from a single-server deployment

Existing users are not disconnected, because the import preserves the original server key pair.

1. Export the backup from the old server: `GET /api/backups/export`.
2. In the panel: **Settings → Import**, enable *Replace existing clients*, upload the file.
3. Check that **Server public key** on the Fleet page matches the old one.
4. Replace the old container on that server with the agent — the install command from
   [Adding a node](getting-started.md#adding-a-node).
5. The agent applies the configuration with `awg syncconf`, so the interface is never brought
   down and existing tunnels survive - unless the subnet or the MTU differs from what the
   interface is already carrying, which no in-place sync can change. Then it restarts the
   interface once and says so in its log.

For a fresh panel you can instead point `AWG_IMPORT_LEGACY_STATE` at a mounted `state.json`.

---

[← Security model](security.md) · [Documentation](../README.md) · [Configuration reference →](configuration.md)

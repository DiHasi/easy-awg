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
```

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

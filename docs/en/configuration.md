[← Documentation](../README.md) · **English** · [Русский](../ru/configuration.md)

# Configuration reference

## Control plane

| Variable | Purpose |
| --- | --- |
| `AWG_ENDPOINT_HOST` | What clients use as the endpoint. **This is the failover switch.** |
| `AWG_PORT` | UDP port clients connect to. Default 51820. |
| `AWG_SUBNET` | Tunnel subnet; addresses are allocated fleet-wide from it. Default `10.8.0.0/24`. |
| `AWG_CLIENT_ALLOWED_IPS` | `AllowedIPs` written into client configs. Default `0.0.0.0/0, ::/0`. |
| `AWG_CLIENT_DNS` | DNS servers written into client configs. |
| `AWG_ADMIN_USER`, `AWG_ADMIN_PASSWORD` | Creates the first admin on an empty database. |
| `AWG_CONTROL_DB` | SQLite file. Default `/etc/awg-control/control.db`. |
| `AWG_BUNDLE_LIFETIME_MINUTES` | How long a signed bundle stays valid. Default 15. |
| `AWG_USAGE_RETENTION_DAYS` | Days of per-peer traffic history kept for the Traffic page. `0` records none. Default 90, capped at 365. |
| `AWG_AVAILABILITY_RETENTION_DAYS` | Days of availability history kept: how each node stood and what each probe saw. `0` records none. Default 90, capped at 365. |
| `AWG_METRICS_TOKEN` | Turns on `/api/metrics` and is the bearer token it requires. Unset leaves it off. |
| `AWG_IMPORT_LEGACY_STATE` | One-shot adoption of an old `state.json`. |
| `AWG_CLOUDFLARE_API_TOKEN` | Lets the panel move the record itself. Needs `Zone:Read` and `Zone:DNS:Edit`. |
| `AWG_CLOUDFLARE_ZONE_ID` | The zone the record lives in. Both are required, or switching stays manual. |
| `AWG_DNS_RECORD_NAME` | Record to move. Defaults to `AWG_ENDPOINT_HOST`. |
| `AWG_DNS_TTL` | TTL written with the record, and the speed limit on failover. Default 60. |
| `AWG_FAILOVER_CHECK_SECONDS` | How often the panel evaluates the fleet. Default 30. |
| `AWG_FAILOVER_GRACE_SECONDS` | How long the active node must be failing before traffic moves. Default 120. |
| `AWG_FAILOVER_COOLDOWN_MINUTES` | Minimum time after any switch before an automatic one. Default 15. |
| `AWG_NODE_STALE_SECONDS` | A node silent this long counts as silent. Default 90. |
| `AWG_PROBE_STALE_SECONDS` | Probe results older than this are ignored. Default 300. |
| `AWG_PROBE_INTERVAL_SECONDS` | How often probes run a round. Default 60. |
| `AWG_PROBE_HANDSHAKE_TIMEOUT_SECONDS` | How long a probe waits for one node's handshake. Default 15. |
| `AWG_PROBE_CHECK_URLS` | What probes fetch through the tunnel, comma-separated; any answer counts. Default `http://1.1.1.1/cdn-cgi/trace`, `http://connectivitycheck.gstatic.com/generate_204`. `none` checks the handshake alone. |
| `AWG_PROBE_TRAFFIC_TIMEOUT_SECONDS` | How long each of those has to answer. Default 8. |
| `AWG_PANEL_ROLE` | `active` or `standby`, and only on an empty database: the live role is stored, so a promotion survives a restart. Default `active`. |
| `AWG_PANEL_URL` | This panel's own base URL, as the other panel reaches it. Required to enroll a standby and required on one - without it a superseded panel can never be told. |
| `AWG_PANEL_PUBLIC_IP` | What the panel record is pointed at when this panel takes over. |
| `AWG_PANEL_NAME` | How this panel is listed in the pair. Defaults to the machine name. |
| `AWG_PANEL_PEER_URL` | On a standby: where to enroll. Afterwards the other panel's own URL arrives with every snapshot. |
| `AWG_PANEL_ENROLLMENT_TOKEN` | The one-time token issued under Nodes -> Panel pair. Spent on first use. |
| `AWG_PANEL_DNS_RECORD_NAME` | The record agents follow to reach the panel, moved on promotion. Unset leaves it to you; nodes stay fail-static meanwhile. |
| `AWG_PANEL_SYNC_SECONDS` | How often a standby pulls. Default 30. |
| `AWG_PANEL_FAILOVER_GRACE_SECONDS` | How long a standby must have failed to pull before it may take over. Must exceed `AWG_PANEL_SYNC_SECONDS`. Default 300. |
| `AWG_PANEL_FAILOVER_COOLDOWN_MINUTES` | Minimum time between one role change here and the next. Default 15. |
| `AWG_PANEL_WITNESS_URLS` | What a standby fetches to check its own uplink, comma-separated; any answer counts. The only witness a panel has, so `none` refuses automatic promotion rather than granting it blind. Defaults to the probe check URLs. |
| `AWG_PANEL_WITNESS_TIMEOUT_SECONDS` | How long each of those has to answer. Default 8. |
| `AWG_PANEL_STREAM_BATCH` | History rows per pull while a standby catches up. Default 5000. |
| `AWG_NOTIFY_WEBHOOK_URL` | Where to POST notifications as JSON. |
| `AWG_NOTIFY_TELEGRAM_BOT_TOKEN`, `AWG_NOTIFY_TELEGRAM_CHAT_ID` | Telegram bot and chat for notifications. |

## Node

| Variable | Purpose |
| --- | --- |
| `AWG_CONTROL_URL` | Where the panel lives. |
| `AWG_ENROLLMENT_TOKEN` | Only needed for the first start. |
| `AWG_EGRESS_INTERFACE` | Leave empty to detect the default-route interface. |
| `AWG_HEALTH_URL` | Agent health endpoint. Binds on the host. Default `http://127.0.0.1:8081`. |
| `AWG_POLL_INTERVAL_SECONDS` | How often to check for a new revision. Default 20. |
| `AWG_NODE_STATE_PATH` | Agent identity and cached bundle. Default `/etc/awg-node`. |
| `AWG_INTERFACE` | Interface name. Default `awg0`. A probe ignores it and uses `awgprobe`. |
| `AWG_INTERFACE_CONFIG_PATH` | Where the generated awg-quick config is written. Default `/etc/amnezia/amneziawg/awg0.conf`. |
| `AWG_BUNDLE_FILE` | A local bundle to apply instead of fetching one, for bootstrap and for testing a node without a panel. Ignored for a probe. |
| `AWG_PUBLIC_IP` | States the node's public address instead of discovering it. |
| `AWG_PUBLIC_IP_URLS` | Echo services used to discover it. Empty switches discovery off. |
| `AWG_PUBLIC_IP_REFRESH_MINUTES` | How often a known address is checked again. Default 10. |
| `AWG_ROLE` | `probe` runs the image as a probe instead of a node. Its state lives in `/etc/awg-probe`. |

Obfuscation is **not** configured through environment variables. It is fleet-wide and lives in the
panel under **Fleet**, so a change applies to every node at once.

---

[← Operations](operations.md) · [Documentation](../README.md) · [Development →](development.md)

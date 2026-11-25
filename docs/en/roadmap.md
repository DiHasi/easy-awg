[← Documentation](../README.md) · **English** · [Русский](../ru/roadmap.md)

# Roadmap and known gaps

## Roadmap

Phase 1 (multi-server with manual switchover) and Phase 2 (probes, blocked-vs-down detection,
automatic failover, notifications) are done, and so is fleet identity rotation - a seized node is
now recoverable from the panel rather than by rebuilding everything by hand. So is
[an export of the panel itself](operations.md#backing-the-panel-up), the file neither half of a
pair could hand you, and [operator accounts](security.md#operator-accounts), so the panel's own
password can be changed from the panel rather than in SQLite on the host. What comes next:

- **Re-pinning the bundle signing key.** Rotation deliberately leaves it alone, because an agent
  that cannot verify a bundle stops converging rather than fetching a new key. Rotating it needs
  a re-pinning handshake of its own.

Cloudflare stays the only DNS provider by choice. Another one, or a floating IP, is a second
implementation of `IDnsRecordUpdater` and nothing else, so it can be added when a deployment
actually needs it.

## Known gaps

- The pair has only been exercised in containers on one host, never across real servers - though
  what `scripts/e2e.sh` covers there is real: agents applying bundles, a probe handshaking and
  fetching through the tunnel, and both kinds of block.
- The frontend's tests cover the formatters, the node state words and the availability ribbon. The
  composables that need a Nuxt app and a stubbed API are still only exercised by hand.
- Automatic failover cannot be exercised end to end in CI: arming it requires a DNS provider, and
  there is none there. The script asserts the recommendation the manual path produces instead,
  which goes through the same policy.
- A panel pair has only been exercised in the integration suite, where both panels run in one
  process and talk over each other's in-memory servers. What that covers is the real protocol -
  signed requests, signed payloads, the ETag gate, the term, the read-only guard, and an agent
  accepting a bundle from the panel it did not enroll with - but it is not two machines, and the
  container script does not bring up a second panel.
- Automatic panel promotion is covered only by its policy tests, which are pure. Driving it end to
  end needs a grace period that has really elapsed and a witness that answers, and a suite that
  reached the internet to decide a test would be worse than the gap.
- Both panels have to carry the same `AWG_USAGE_RETENTION_DAYS` and
  `AWG_AVAILABILITY_RETENTION_DAYS`: each prunes its own copy of the history, so a standby set
  lower drops rows it will not fetch again. Clearing the availability history is local for the
  same reason - the standby replicates it forwards from a cursor and keeps the rows it already
  has, so a promoted standby brings them back and has to be cleared in its turn.
- `/api/metrics` answers on either panel from that panel's own copy of the data, so scraping both
  double-counts every series. Scrape one, or the panel record.
- [Per-address checks](failover.md#every-entry-and-exit-address) diagnose every discovered entry and
  exit, but never change a client's pin or select a secondary entry automatically. Failover still
  judges the primary public IP. NAT translation can only be verified against a known outside address.
- Egress addresses are IPv4 only, like the rest of the tunnel, and a changed address applies to
  connections made after it: the ones already open keep the address they were established with
  until they close.
- The predecessor single-server app is preserved at tag `v0.9-standalone` and has been removed
  from the tree.

---

[← Development](development.md) · [Documentation](../README.md)

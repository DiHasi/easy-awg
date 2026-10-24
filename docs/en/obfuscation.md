[← Documentation](../README.md) · **English** · [Русский](../ru/obfuscation.md)

# Obfuscation and MTU

## Obfuscation (AmneziaWG 3.x)

The panel targets the AmneziaWG 3.1 line. 3.x exists because masking individual traits stopped being
enough: a tunnel with randomized packet sizes but a fixed rekey interval and a fixed keepalive
cadence is still recognizable. So most of the new settings are *ranges* rather than values, written
as `lo-hi`, and the peer re-rolls within the range on every use. A single number still works, and
`0`, `0-0` or `off` means "behave like stock WireGuard".

The settings divide in two, and which half a setting is in decides where you configure it.

**Must match on both ends** - fleet-wide, written into every node interface and every client config:

| Setting | Notes |
| --- | --- |
| `S1`-`S4` | Random padding on init, response, cookie and transport messages. |
| `H1`-`H4` | Message type identifiers. Now ranges; the four may not overlap, and may not enter the 1-4 range WireGuard reserves. |
| `HeaderProtectionKey` | New in 3.0. ChaCha20 over the packet header, so the message type is unreadable rather than merely renamed. Needs `S1`-`S4` of at least 12: the nonce rides in that padding. Generate one with the button next to the field. |
| `RandomTrailers` | New in 3.0. Random trailing bytes on every packet. |

**May differ per client** - the fleet profile holds the default, and any client can override it:

| Setting | Notes |
| --- | --- |
| `Jc`, `Jmin`, `Jmax` | Junk packets before the handshake. |
| `I1`-`I5` | Obfuscation packets sent before the handshake, as a tag chain: `<b hex>`, `<t>`, `<r n>`, `<rc n>`, `<rd n>`, `<d>`, `<ds>`, `<dz>`. |
| `ContentPaddingAddition` | New in 3.0. Extra random payload bytes on top of the 16-byte multiple WireGuard already pads to. |
| `RekeyAfterTime` | Seconds before a live session re-handshakes. WireGuard uses a fixed 120. |
| `RekeyTimeout` | Seconds between handshake retries. Fixed 5 in WireGuard. |
| `RejectAfterTime` | Seconds before a key is abandoned. Fixed 180. Must stay above `RekeyAfterTime`. |
| `KeepaliveTimeout` | Seconds of silence before a keepalive. Fixed 10. |
| `MaxHandshakeAttempts` | Attempts before the peer gives up. Fixed 18. |
| `DisableCookies` | New in 3.0. Stops answering with a cookie reply under load, which is a recognizable message of its own. |
| `PersistentKeepalive` | Goes in the client `[Peer]`. Defaults to 25; a range here stops the whole fleet emitting one synchronized heartbeat. |

Differing per client is the point rather than a nicety: two clients that rekey on the same schedule
and pad to the same length are a correlatable pair.

The fleet profile's `Default*` values are not only the seed a new client starts from — the node's
own interface runs them too, for the timing and padding half (`ContentPaddingAddition`, the rekey
and keepalive ranges, `MaxHandshakeAttempts`, `DisableCookies`). A server that rekeys on a fixed
schedule is as fingerprintable as a client that does, and it has no client to override them.
`Jc`/`Jmin`/`Jmax` and `I1`-`I5` are the exception: they describe what an initiator sends, so they
never reach a node at all — it only has to tolerate them.

Changing `HeaderProtectionKey`, `RandomTrailers`, `S1`-`S4` or `H1`-`H4` changes the wire format, so
**every client config has to be handed out again** - existing ones stop handshaking. The per-client
half can be changed freely; only that client's config needs reissuing.

## Tunnel MTU

**Settings → Tunnel MTU**, default **1280**, accepted range **1280-1420**. It is written into every
client config and applied by every node, and the default is the one AmneziaWG's own 3.1 upgrade
guide recommends. 1420 is the ceiling because that is what `awg-quick` would have worked out by
itself on a 1500 underlay, and 1280 is the floor because it is the smallest MTU IPv6 requires a
link to carry.

Left alone, `awg-quick` works out route-MTU minus 80, which is 1420 on a 1500 underlay, and client
apps size their own default the same way. Those 80 bytes cover vanilla WireGuard - 20 IPv4 (40
IPv6) + 8 UDP + 16 data header + 16 Poly1305 - and not what 3.x adds to every transport packet on
top: `S4`, `ContentPaddingAddition`, and `RandomTrailers`, which 3.1 turns on by default. The outer
datagram then crosses 1500 and fragments, and mobile CGNAT and plenty of home routers drop IP
fragments. The result is a tunnel that works for most people and loses, for whoever sits behind a
constrained path, exactly the traffic that sustains full-size packets at high bitrate - video,
YouTube first, over QUIC, while everything smaller keeps working and hides the cause.

Two things to know before changing it:

- **It bumps the fleet revision, and each node restarts its interface once** as it picks the change
  up. `awg syncconf` cannot change an interface's MTU, so the agent brings the interface down and
  back up. Nodes poll independently, so the restarts stagger.
- **Configs already handed out keep their old MTU** until they are downloaded again. The renderer
  runs at download time, so re-sending a peer's share link is enough - no new peer, no new keys.

A node whose own egress is below 1500 can run less than the fleet value; that override lives on the
node row (*Node settings*) and the fleet value does not raise it. It is held to the same 1280-1420
range, so the override is only useful on a fleet that runs above 1280 — the floor is the floor for
both. Unlike the fleet value, it changes what one node is handed rather than every client config,
so only that node restarts its interface.

## Upgrading a fleet to 3.x

Upgrade the panel first, then walk the nodes. Until a node is upgraded it keeps reporting the older
bundle schema, and the panel serves it a profile with the 3.x settings stripped and `H1`-`H4`
collapsed to their low bound, so its tunnel stays up on the settings it was already running. Such a
node is flagged on the **Nodes** page as `agent predates AmneziaWG 3.x`; do not turn on the 3.x
wire-format settings until that flag is gone from every node, or those nodes will be serving a wire
format your clients no longer speak.

On a node using the kernel data path, `scripts/install-awg-v3.sh` upgrades `awg`, `awg-quick` and
the DKMS module together, and verifies a real 3.x configuration on a throwaway interface before
leaving the module in place - rolling back to the userspace path if it cannot. Nodes running the
bundled `amneziawg-go` need nothing beyond the new image.

---

[← A second panel](panel-pair.md) · [Documentation](../README.md) · [Security model →](security.md)

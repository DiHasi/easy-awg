[← Documentation](../README.md) · **English** · [Русский](../ru/demo.md)

# The demo account

A read-only account that signs in to the panel like any other and sees the fleet exactly as an
operator does — the same pages, the same nodes, the same states, the same graphs — except that
every name, address and key on the way out has been replaced with a stand-in.

It exists for the case where you want to show somebody what the panel does without showing them
anybody in it: a public demo link, a screenshot for a write-up, a colleague who should see how the
failover reads before being given an account of their own.

## Turning it on

```bash
AWG_DEMO_USER=demo
AWG_DEMO_PASSWORD=<something long>
```

Running under compose, those two lines go in the `.env` beside `compose.control.yaml`, which
passes them through - and `docker compose up -d` rather than `restart`, because a restart does not
re-read `.env`.

The account is created on the next start of the panel in charge, and after that it is an ordinary
admin row carrying one flag. That matters in three ways:

- It survives a restart, so removing the environment variables does not remove the account.
- It replicates to a standby panel with the rest of the admin rows, flag included — a promoted
  standby does not hand the demo a full panel.
- An account that already exists is never touched. If `AWG_DEMO_USER` names an account that is
  already a full admin, the panel logs a warning and leaves it exactly as it is: nothing here will
  quietly take an operator's own account away, and nothing will put a full admin behind a password
  published next to a demo link. Pick another name, or delete that row.

A password is required even though the account can change nothing. What a demo session may read is
still every state, every count and every graph of a real fleet, which is more than an unlisted URL
should be handing out to whoever finds it.

## What it cannot do

Every request that is not a read is refused with `403` and the code `demo_read_only`. That is a
rule about the method rather than a list of endpoints, so an endpoint added later is covered
without anybody having to remember this page exists. Signing out is the one exception, because
refusing every `POST` would leave a visitor unable to leave.

`/api/metrics` is not served to a demo session either. It answers in Prometheus text rather than
JSON, which the masking does not read, and a scrape is not a page.

The panel's own pages still draw their buttons. Pressing one shows the refusal rather than doing
nothing, which is the honest outcome: the account is read-only on the server, not merely
discouraged in the browser.

## What is replaced

| | Shown as |
| --- | --- |
| Peer and group names | A stand-in person — "Aurora Finch" |
| Node, probe and panel names | A stand-in place — `fra-2`, `ams-probe`, `zrh-panel` |
| Operator names in the log | `operator3` |
| Tunnel addresses | The same host, on a different private subnet |
| Public addresses | The documentation ranges (RFC 5737, RFC 3849) |
| Hostnames, endpoint hosts, DNS record names, panel URLs | A name under `example.net` |
| Public keys, preshared keys, the server public key | A structurally valid 32-byte key |
| The tunnel subnet | A different private subnet of the same size |
| S1-S4, H1-H4, the header protection key | A different, valid wire format |
| Sentences the panel wrote about any of the above | The same sentence, about the stand-ins |

Two properties are worth stating because they are what make the demo readable rather than merely
redacted.

**One peer is one person everywhere.** The stand-in is derived from the real value, so the peer at
the top of the list is the same person in the traffic table, in the config it hands out and in the
line in the log that says when it was created. A demo where each page invented its own names would
be noise.

**The derivation cannot be run backwards.** It goes through a key drawn once per process, which is
never written to disk — so the stand-ins hold for as long as the panel is up and are different
after a restart, and nothing a visitor can see tells them what the real value was.

The obfuscation profile is split the way the rest of the panel splits it. S1-S4, H1-H4 and the
header protection key describe the wire format: they are what makes this fleet's packets
recognizable as this fleet's, which is why a rotation draws them afresh and why a demo shows a
different set. The per-peer half — junk counts, padding and timing ranges — differs per peer by
design, so it describes nobody and comes through untouched.

## What is not replaced

Everything the demo is for: how many peers there are, how many nodes, which node is active, which
is blocked and from where, how much traffic moved and when, uptime per node and per probe, the
fleet revision and generation, and every timestamp.

Also untouched, because masking them would break the panel rather than protect anybody: internal
ids, event kinds and status words, routing constants like `0.0.0.0/0`, and agent versions.

## The config download

A demo session can open a peer's **Config** dialog and get a config. It is rendered from stand-in
material rather than scrubbed afterwards — a stand-in peer, on a stand-in subnet, with stand-in
keys, pointed at a stand-in endpoint — so it is a real config in every respect except whose it is.
It will not connect to anything, which is the point.

Nothing is recorded by it. Normally rendering a config stamps which fleet generation the peer was
handed, because that is how the panel knows who still has to be found after a rotation; a visitor
looking at a stand-in was handed nothing, and a stamp written for them would send an operator
chasing somebody who is holding exactly what they were before.

Share links cannot be created: that is a write, and it is refused like the rest.

## What it still tells somebody

Be deliberate about this. The demo conceals *who*, not *what*. Somebody looking at it learns how
many people you serve, how many nodes you run, how often they get blocked and in whose network,
and how much traffic goes through them. On a small fleet that is not nothing.

If that is more than you want to publish, the demo is the wrong tool and a seeded panel of your
own is the right one.

## On a standby

A demo account works on a standby panel too, from that panel's replicated copy. Both refusals
apply, and they say different things on purpose: a standby answers `503` because the request
arrived at the wrong panel and is worth retrying at the other one, while a demo session is refused
`403` because of who it is, which no amount of waiting changes.

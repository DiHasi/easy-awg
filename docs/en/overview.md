[← Documentation](../README.md) · **English** · [Русский](../ru/overview.md)

# Overview

A self-hosted control plane for a fleet of AmneziaWG VPN servers.

One panel is the source of truth for clients, keys and obfuscation settings. Agents on each VPN
server pull that configuration and converge onto it. The point of the fleet design is **seamless
failover**: when a server is blocked or dies, you move traffic to another one without reissuing a
single client config.

> Status: the fleet architecture is in place, and so is everything built on top of it — external
> probes, blocked-vs-down detection, automatic failover, notifications, the availability history,
> fleet identity rotation and the panel pair. Failover stays a button you press until you arm it
> yourself. See [Roadmap and known gaps](roadmap.md).

## How it works

Every node runs the **same AmneziaWG server identity** — the same key pair, subnet and obfuscation
profile. A client config pins the server public key, so pointing DNS at a different node does not
change what the client is talking to. It just keeps working.

```
                    ┌─────────────────────────────┐
                    │        awg-control          │
                    │  source of truth + admin UI │
                    └──┬───────────────────┬──────┘
             pull HTTPS│                   │DNS / floating IP
              ┌────────┴────────┐          ▼
              ▼                 ▼      clients follow
        ┌──────────┐      ┌──────────┐  the endpoint
        │ awg-node │      │ awg-node │
        │  + awg0  │      │  + awg0  │
        └──────────┘      └──────────┘
```

Nodes only ever make **outbound** calls, so they open no management port — just the UDP tunnel.
And they are **fail-static**: if the panel is unreachable, an agent keeps serving traffic from its
cached configuration indefinitely. Losing management never means losing the VPN.

A node receives only what it needs to serve peers — public keys, preshared keys and addresses.
Client private keys and client names never leave the control plane.


---

[Documentation](../README.md) · [Getting started →](getting-started.md)

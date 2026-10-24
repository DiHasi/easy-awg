# AWG Easy

A self-hosted control plane for a fleet of AmneziaWG VPN servers.

One panel is the source of truth for clients, keys and obfuscation settings. Agents on each VPN
server pull that configuration and converge onto it. The point of the fleet design is **seamless
failover**: when a server is blocked or dies, you move traffic to another one without reissuing a
single client config.

📖 **[Documentation](docs/README.md)** — every page in English and Russian ·
📖 **[Документация](docs/README.md)** — каждая страница на английском и русском

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

Three properties follow from that shape, and they are what the rest of the design protects:

- **Nodes make only outbound calls.** They open no management port — just the UDP tunnel.
- **Nodes are fail-static.** If the panel is unreachable, an agent keeps serving traffic from its
  cached configuration indefinitely. Losing management never means losing the VPN.
- **A node learns only what it needs to serve peers** — public keys, preshared keys and addresses.
  Client private keys and client names never leave the control plane.

A blocked node looks perfectly healthy from the inside, so the panel does not take its word for it:
external **probes** run from where your clients are, complete a real handshake and fetch through
the tunnel, and the panel combines that with the agent's own reports to tell *blocked* from *down*.
Once an operator arms it, it moves the DNS record on its own. The panel itself can be paired with a
**standby** that replicates the fleet and takes over on one click.

> **Status:** the fleet architecture is in place, and so is everything built on top of it — probes,
> blocked-vs-down detection, automatic failover, notifications, availability history, fleet
> identity rotation and the panel pair. Failover stays a button you press until you arm it
> yourself. See [Roadmap and known gaps](docs/en/roadmap.md).

## Quick start

```bash
cp .env.control.example .env      # set AWG_ENDPOINT_HOST and the bootstrap admin at minimum
docker compose -f compose.control.yaml up -d
```

The panel is then at `http://<host>:8080`. Put TLS in front of it before a node enrolls — over
plain HTTP it would hand the fleet private key to agents in the clear. Then add a node from
**Nodes → Enroll node**, which hands you a one-line install command for that server.

The full version, including what to set and why, is in
[Getting started](docs/en/getting-started.md) · [Запуск](docs/ru/getting-started.md).

## Layout

| Project | Role |
| --- | --- |
| `src/AwgEasy.Contracts` | Wire contract shared by both sides. Changing it changes the fleet. |
| `src/AwgEasy.Node` | Agent for each VPN server, and the probe. Native AOT, privileged, no UI. |
| `src/AwgEasy.Control` | Panel: database, admin API, agent API, serves the frontend. |
| `frontend` | Nuxt 4 + Nuxt UI, generated to static files. |
| `tests/AwgEasy.Tests` | Unit tests plus integration tests hosting the real control plane. |
| `docs` | This documentation, in English and Russian. |

[AGENTS.md](AGENTS.md) holds the architectural invariants worth knowing before changing code.

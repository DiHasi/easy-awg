[← Documentation](../README.md) · **English** · [Русский](../ru/getting-started.md)

# Getting started

## What you need

- **A host for the panel.** Any small VPS. Keep it off your VPN nodes: it holds the identity of
  the whole fleet, and that should not sit on the servers most likely to be blocked or seized.
- **At least one VPN node.** Root access, Docker, `/dev/net/tun`, and the UDP port open. The
  agent uses host networking, so it also takes `127.0.0.1:8081` on that server for its health
  endpoint (configurable via `AWG_HEALTH_URL`).
- **A DNS record you control.** `AWG_ENDPOINT_HOST` goes into client configs, and failover means
  repointing it. Set the TTL to 30–60 seconds ahead of time. Give the panel a Cloudflare API
  token and it moves the record for you; without one it tells you what to change. If your provider
  offers a floating IP, prefer it — reassignment is instant and completely invisible to clients.
- **TLS in front of the panel.** Over plain HTTP it would hand the fleet private key to agents in
  the clear. Put Caddy or nginx with Let's Encrypt in front of it.

## Running the control plane

```bash
cp .env.control.example .env
```

Edit `.env`. At minimum set `AWG_ENDPOINT_HOST` — the public hostname clients will connect to —
and the bootstrap admin credentials. The panel refuses to start without an endpoint host, because
that value is written into every client config and a wrong one produces configs that connect to
nothing.

```bash
docker compose -f compose.control.yaml up -d --build
```

Building on its own needs no configuration, so you can verify the images first:

```bash
docker compose -f compose.control.yaml build
docker compose -f compose.node.yaml build
```

### Do not build on a small VPN node

Building pulls the .NET SDK, a Node image and a Go toolchain, which together want several
gigabytes of disk that a 10 GB server does not have. The compose files reference published images
for this reason: build once somewhere with room, push, and pull on the servers.

```bash
# On a build machine or in CI. AGENT_VERSION is what each node reports back to
# the panel, and PANEL_VERSION is what one panel of a pair reports to the other;
# leave either out and that side identifies itself as 0.0.0-dev.
# The panel is at 1.13.0 while the node image stays at 1.4.3: nothing under src/AwgEasy.Node
# or src/AwgEasy.Contracts changed in it. Rebuild the node image when something under
# src/AwgEasy.Node, src/AwgEasy.Contracts or docker/node.Dockerfile does, not when only the
# panel moved.
# Both images are multi-arch (amd64 and arm64 - a probe on a Raspberry Pi), built on an amd64
# machine with a buildx builder that can push manifest lists:
#   docker buildx create --name awg --driver docker-container
# The builder is named on every line below rather than selected once: Docker Desktop's
# default builder uses the "docker" driver, which refuses a two-architecture build
# outright, and a shell where the default has reset back to it is the normal case.
docker buildx build --builder awg --platform linux/amd64,linux/arm64 -f docker/control.Dockerfile \
    --build-arg PANEL_VERSION=1.13.0 -t dihasi/awg-control:1.13.0 -t dihasi/awg-control:latest --push .
docker buildx build --builder awg --platform linux/amd64,linux/arm64 -f docker/node.Dockerfile \
    --build-arg AGENT_VERSION=1.4.3 -t dihasi/awg-node:1.4.3 -t dihasi/awg-node:latest --push .
```

```bash
# On the servers
docker compose -f compose.control.yaml pull && docker compose -f compose.control.yaml up -d
```

A node then pulls only its runtime image, not the toolchains that produced it.

On first start it creates the database, generates the fleet identity and the bundle signing key,
and creates the admin account. The panel is then at `http://<host>:8080`.

Without `AWG_ADMIN_USER` / `AWG_ADMIN_PASSWORD` the panel still starts but refuses every admin
request, with a loud warning in the log. That is deliberate: refusing to boot because of a typo in
an environment variable would be worse than starting locked.

## Adding a node

In the panel: **Nodes → Enroll node**. You get a ready-to-run command:

```bash
curl -fsSL https://panel.example.com/install.sh | sh -s -- --url https://panel.example.com --token <token>
```

The command carries no tunnel port. The agent runs with host networking and takes its port from
the fleet configuration, so changing `AWG_PORT` on the panel moves every node without touching a
single install command.

The enrollment token is **shown once**, cannot be recovered, and expires in 30 minutes. The
installer sets up Docker if needed, enables forwarding and starts the agent, which enrolls itself,
pins the panel's signing key and pulls the fleet configuration. Re-running it is safe — it
upgrades the agent in place and keeps the node's identity, so the tunnel is not disturbed.

It takes two more flags, both optional. `--public-ip 203.0.113.7` states the server's public
address instead of letting the agent discover it, for a host behind static NAT or with no route out
to an echo service. `--image` runs an image other than `dihasi/awg-node:latest`, which is also what
`AWG_NODE_IMAGE` in the installer's environment sets.

If you prefer compose, use `.env.node.example` with `compose.node.yaml`.

### Per-host tweaks

Do not edit the tracked compose files on a server: every `git pull` will then conflict. Put local
deviations in an override file, which is gitignored, and pass both:

```bash
docker compose -f compose.control.yaml -f compose.control.override.yaml up -d
```

```yaml
# compose.control.override.yaml
services:
  awg-control:
    ports:
      - "127.0.0.1:8080:8080"   # only reachable through the reverse proxy
```

Plain values — hostnames, ports, credentials — belong in `.env` instead.


---

[← Overview](overview.md) · [Documentation](../README.md) · [Using the panel →](panel.md)

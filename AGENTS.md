# AGENTS.md

Guidance for coding agents working in this repository.

## What this is

A self-hosted control plane for a fleet of AmneziaWG VPN servers. One panel is the source of
truth; agents on each VPN server converge onto the configuration it publishes. The point of the
fleet design is **seamless failover**: when a node is blocked or dies, traffic moves to another
node without reissuing a single client config.

The project replaced a single-server panel with this architecture. Phase 1 (multi-server with
manual switchover) is complete. Phase 2 (health probes, blocked-vs-down detection, automatic
failover) is not started.

The predecessor is preserved at tag `v0.9-standalone` and is no longer in the tree. When touching
the legacy import path, that tag is where its `state.json` format is defined.

## Layout

```
src/AwgEasy.Contracts/   Wire contract shared by both sides. Changing it changes the fleet.
src/AwgEasy.Node/        Agent on each VPN server. AOT-published, privileged, no UI.
src/AwgEasy.Control/     Panel: database, admin API, agent API, serves the frontend.
frontend/                Nuxt 4 + Nuxt UI, generated to static files, served by Control.
tests/AwgEasy.Tests/     xUnit. Unit tests plus integration tests hosting the real Control.
docker/                  node.Dockerfile, control.Dockerfile
scripts/install.sh       Node enrollment one-liner, served by Control at /install.sh
```

## Commands

```bash
dotnet build Awg-easy.sln          # whole solution
dotnet test Awg-easy.sln           # 62 tests, all must pass
cd frontend && pnpm run lint       # eslint
cd frontend && pnpm run typecheck  # nuxt typecheck - catches real API/UI type drift
cd frontend && pnpm run generate   # static build into .output/public
```

Run `pnpm run typecheck` after touching anything the frontend consumes. It catches null/undefined
drift between the API and the forms that plain linting does not.

Both images build and have been run together: an agent enrolls, pulls a bundle and brings up
awg0, and the panel reports it healthy and in sync. Build them off the VPN nodes though — the
toolchains want several gigabytes that a small server does not have.

The Go stage tracks amneziawg-go master, whose go.mod can raise the required Go version at any
time. `GOTOOLCHAIN=auto` is set so that does not become a hard build failure.

## Invariants

These are load-bearing. Breaking one is not a style issue — it breaks the fleet, silently.

**The fleet identity is shared by every node.** All nodes run the same AmneziaWG server key pair,
subnet and obfuscation profile. That is precisely what makes failover invisible: a client config
pins the server public key, so switching endpoints does not change what the client is talking to.
Never regenerate the fleet identity as a side effect of anything. Importing a legacy deployment
must preserve its key pair, or every existing user is disconnected.

**The bundle carries no client private keys and no client names.** A node needs only public keys,
preshared keys and addresses to build its peer list. Nodes hold the fleet private key, so a seized
or snapshotted node must not additionally reveal who the clients are. There is a test asserting
this on the raw payload bytes; do not weaken it.

**Bundle JSON goes through `ContractsJsonContext` on both sides.** The signature covers the
payload bytes verbatim. If the control plane and the agent serialize the same bundle differently,
verification fails. Never re-serialize a bundle to verify it.

**Bundle format changes require a synchronized fleet upgrade.** Bump
`DesiredStateBundle.CurrentSchemaVersion`, and keep the control plane able to serve agents one
version behind — you will always upgrade the panel before you walk every node.

**Nodes are fail-static.** If the control plane is unreachable, the agent re-applies its cached
bundle and keeps serving traffic indefinitely. Losing management is never a reason to lose the
data plane. Do not add TTLs, config expiry-on-disk, or peer teardown on connection loss.

**Address allocation belongs to the control plane only.** Because every node runs the same
identity, a client must work on any node, so addresses must be unique fleet-wide. Never allocate
on a node.

**Signed bundles need rollback protection, not just signatures.** A correctly signed bundle stays
valid forever, so `BundleGuard` also enforces monotonic revision, node binding, and expiry. Any
new signed message needs the same treatment.

**Bump the fleet revision for anything that changes what a node runs.** Client create/delete/
enable/disable and obfuscation changes bump it. Renaming a client deliberately does not — it
changes no peer material.

**Never hardcode the egress interface.** NAT masquerades through the default-route interface,
detected at runtime. The old code assumed `eth0`, which silently broke NAT on hosts using `ens3`
or `enp1s0`: the tunnel came up, peers handshook, and no traffic reached the internet.

## Platform notes

**AOT is asymmetric and deliberate.** `AwgEasy.Node` is AOT-published (it ships to every server);
`AwgEasy.Control` is not (it is deployed once, and AOT would fight the auth and data libraries).
In Contracts and Node, never use the reflection-based `JsonSerializer` overloads — pass a
`JsonTypeInfo`. `dotnet publish src/AwgEasy.Node` must stay free of IL2026/IL3050 warnings.

**.NET 10 has no Ed25519 in the BCL.** Bundle and request signing use ECDSA P-256 / SHA-256 with
IEEE P-1363 fixed-field signatures, which is in the BCL and AOT-clean. Do not add a third-party
crypto library to the node image for this.

**Key generation shells out to `awg`.** It happens only in the control plane, whose image carries
the binary. Tests substitute `IAwgKeyGenerator`, so the suite runs on machines without it.

**Agent requests are authenticated by signature, not a bearer token.** The agent's private key
never crosses the wire, and unlike mTLS this survives a TLS-terminating proxy or CDN in front of
the panel. `AgentRequestSignature` is shared by both sides so they cannot disagree on what was
signed; it covers a hash of the body. Node revocation is a flag checked per request — no CRL.

## Conventions

- Code, comments and commit messages are in English. The maintainer communicates in Russian.
- Comments explain **why**, not what. Match the surrounding density; do not narrate obvious code.
- Endpoints return `ApiError { code, message }` on failure; codes are snake_case and stable.
- Timestamps are stored in SQLite as ISO-8601 round-trip strings so the file stays inspectable.
- No ORM. Hand-written SQL via `Microsoft.Data.Sqlite` and the helpers in `SqliteExtensions`.
- Frontend: Nuxt UI components, semantic Tailwind tokens (`text-muted`, `border-default`, …).
  The API models "unset" as `null`, form inputs need `undefined` — convert at the boundary.
- Everything under `/api` requires an authenticated admin except `/health`, `/auth/*` and
  `/shares/*`. Do not add an endpoint to the anonymous set without a reason worth stating.

## Testing expectations

Integration tests host the real control plane against a throwaway SQLite file and drive it with a
stand-in agent that signs requests and verifies bundles exactly as the real one does. When you
touch the agent protocol, auth, or the bundle, extend those rather than mocking around them.

Assertions should not depend on test execution order. The fixture is shared per test class, so
never assert on a specific allocated address — assert on what the API returned.

## Known gaps

- The pair has only been exercised in containers on one host, never across real servers.
- The frontend has no automated tests.
- `Microsoft.OpenApi` 2.0.0 arrives transitively with a known high-severity advisory (NU1903).
- Phase 2 (external probes, blocked-vs-down detection, DNS/floating-IP failover, notifications)
  is designed but not built. Failover is manual today.

# AGENTS.md

Guidance for coding agents working in this repository.

## What this is

A self-hosted control plane for a fleet of AmneziaWG VPN servers. One panel is the source of
truth; agents on each VPN server converge onto the configuration it publishes. The point of the
fleet design is **seamless failover**: when a node is blocked or dies, traffic moves to another
node without reissuing a single client config.

The project replaced a single-server panel with this architecture. Phase 1 (multi-server with
manual switchover) is complete: the panel picks the active node and moves the DNS record clients
follow, through Cloudflare when a token is configured. Phase 2 (health probes, blocked-vs-down
detection, automatic failover) is not started - what is missing is the judgement about when to
switch, not the switch itself.

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
dotnet test Awg-easy.sln           # 140 tests, all must pass
cd frontend && pnpm run lint       # eslint
cd frontend && pnpm run typecheck  # nuxt typecheck - catches real API/UI type drift
cd frontend && pnpm run generate   # static build into .output/public
```

Run `pnpm run typecheck` after touching anything the frontend consumes. It catches null/undefined
drift between the API and the forms that plain linting does not.

Both images build and have been run together: an agent enrolls, pulls a bundle and brings up
awg0, and the panel reports it healthy and in sync. Build them off the VPN nodes though — the
toolchains want several gigabytes that a small server does not have.

Both images pin the AmneziaWG refs (`AMNEZIAWG_TOOLS_REF`, `AMNEZIAWG_GO_REF`) to the 3.1 line
rather than tracking master, which already carries an awg4 branch. Bump the two together.
`GOTOOLCHAIN=auto` stays set because amneziawg-go can raise the required Go version at any time,
and the golang images pin `GOTOOLCHAIN=local`, which would turn that into a hard build failure.

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
`DesiredStateBundle.CurrentSchemaVersion`, raise `MinimumSupportedSchemaVersion` only when you drop
support, and keep the control plane able to serve agents one version behind — you will always
upgrade the panel before you walk every node. Negotiation is server-side state, not a request
parameter: the agent reports its highest supported schema on enrollment and on every status report,
the panel stores it on the node row, and `FleetService.BuildBundle` issues the newest bundle that
node can apply. Do not move that decision into a query string or a header — both sit outside the
request signature, so either would let a downgrade be forced from the network.

**Nodes are fail-static.** If the control plane is unreachable, the agent re-applies its cached
bundle and keeps serving traffic indefinitely. Losing management is never a reason to lose the
data plane. Do not add TTLs, config expiry-on-disk, or peer teardown on connection loss.

That is why `BundleGuard` takes a `BundleOrigin`. `ExpiresAt` is replay protection for a bundle
arriving over the network and is enforced there; it is deliberately *not* enforced on a bundle read
from the node's own disk - the cached one, one handed over as `AWG_BUNDLE_FILE`, or one being
inspected with `--render-bundle`. Bundles live 15 minutes, so enforcing it on the cache meant a node
rebooting more than that after its last poll refused its own configuration and brought no interface
up at all, precisely when the control plane was unreachable. Getting a stale bundle into that cache
requires root on the node, which already defeats every check here, and the monotonic revision check
still refuses a rollback. Do not collapse the two paths back together.

**Address allocation belongs to the control plane only.** Because every node runs the same
identity, a client must work on any node, so addresses must be unique fleet-wide. Never allocate
on a node.

**Signed bundles need rollback protection, not just signatures.** A correctly signed bundle stays
valid forever, so `BundleGuard` also enforces monotonic revision, node binding, and expiry. Any
new signed message needs the same treatment.

**Obfuscation settings split into "must match" and "may differ", and the split is load-bearing.**
S1-S4, H1-H4, `HeaderProtectionKey` and `RandomTrailers` describe the wire format: a client and
the node it talks to must carry identical values or the handshake is never recognized, so they are
fleet-wide and written into both configs by `AppendInterfaceObfuscation`. Everything else — junk
packets, I1-I5, and the 3.x padding and timing ranges - may differ per peer, and differing is the
point: two clients that rekey on the same schedule are a correlatable pair. Those live on
`ClientObfuscationOverrides`, with the fleet profile carrying the `Default*` seed. When you add a
setting, decide which half it belongs to first; putting a must-match setting in the second half
produces a fleet that half-works.

**A node applies its bundle in one shot, so one bad value costs the whole tunnel.** `awg` rejects
a device configuration as a unit — the interface does not come up with the rest of the settings
honoured, it does not come up at all. That is why `AwgObfuscationValidator` mirrors amneziawg's own
rules rather than trusting the form: H1-H4 ranges may not overlap and may not enter the 1-4 range
WireGuard reserves, S1-S4 must be at least 12 when `HeaderProtectionKey` is set (the ChaCha20
nonce rides in that padding), and `RejectAfterTime` must stay above `RekeyAfterTime`. The legacy
import path validates too, and drops an unusable profile with a warning rather than poisoning the
fleet with it.

**Switching the active node is not a fleet change.** Failover repoints one DNS record; every
client config already names that host and pins the fleet public key, so the same key, subnet and
obfuscation profile answer at a different address. Nothing is reissued and no node re-applies
anything, so `POST /nodes/{id}/activate` deliberately does not bump the revision. If you ever find
yourself bumping it there, the model has drifted.

**The stored active node means "the record points here".** `FleetRepository.SetActiveNode` is
written only after the provider confirms the edit; a provider that refuses leaves the previous
active node in place and the call fails. A panel that claims a node is active while the record
still answers with another one is worse than no claim at all - it is the one fact an operator
checks under pressure. `DnsStatusResponse.Matches` is the second opinion, resolved live, and it is
advisory: the panel's own resolver caches like any other.

**A node's public address comes from the node.** The agent asks an outside echo service and
reports the answer. Do not switch this to the source address of the agent's request - a
TLS-terminating proxy or CDN in front of the panel replaces it, and the panel would then point DNS
at the CDN. A failed lookup reports null and the stored address is kept (`COALESCE` in
`RecordStatus`), because forgetting an address over one flaky lookup drops that node out of the
failover rotation. `PublicIpAddress` rejects anything unroutable, so a NAT-ed answer never becomes
a DNS target; an explicit `AWG_PUBLIC_IP` bypasses that check on purpose.

**Status-report fields are not bundle fields.** `NodeStatusReport` is not signed content shared
byte for byte, so adding an optional field there needs no synchronized fleet upgrade: an older
agent omits it and reads as null. The bundle rules above still apply to anything inside
`DesiredStateBundle`.

**Bump the fleet revision for anything that changes what a node runs.** Client create/delete/
enable/disable and obfuscation changes bump it. Renaming a client deliberately does not — it
changes no peer material.

**Never hardcode the egress interface.** NAT masquerades through the default-route interface,
detected at runtime. The old code assumed `eth0`, which silently broke NAT on hosts using `ens3`
or `enp1s0`: the tunnel came up, peers handshook, and no traffic reached the internet.

**`awg syncconf` applies the device, not the interface.** It carries keys, listen port,
obfuscation and peers. The address, the MTU and the `PostUp`/`PostDown` hooks belong to
awg-quick, and `awg-quick strip` removes them before syncconf ever sees them. So an interface
that outlives the config that created it — a node re-enrolled into a fleet on a different subnet,
most of all — keeps the old address forever while the agent reports every revision as applied,
and the panel shows it healthy and in sync. What it actually does is decrypt client packets and
drop every one for failing the `AllowedIPs` check: `received` climbs, `sent` stays at zero.
`AwgInterface` therefore compares the running address and MTU against the bundle before it
chooses a path, and takes the interface down *before* overwriting the config file, so the old
`PostDown` withdraws the rules its own `PostUp` installed. Anything else you add that syncconf
cannot apply belongs in that comparison too.

**The node's firewall rules are checked on every apply, not just installed by `PostUp`.** The
hooks run only at bring-up, and syncconf runs no hooks, so a rule flushed by a Docker daemon
restart — or an interface that was already up when the agent arrived — would otherwise never come
back. `NodeFirewall.Rules` is the single list both the hooks and the `iptables -C` check are
built from; keep it that way, or the check stops matching what the hook wrote and every apply
appends a duplicate.

## Platform notes

**AOT is asymmetric and deliberate.** `AwgEasy.Node` is AOT-published (it ships to every server);
`AwgEasy.Control` is not (it is deployed once, and AOT would fight the auth and data libraries).
In Contracts and Node, never use the reflection-based `JsonSerializer` overloads — pass a
`JsonTypeInfo`. `dotnet publish src/AwgEasy.Node` must stay free of IL2026/IL3050 warnings.
That is enforced, not just asked for: Node and Contracts promote the IL trim/AOT codes to
errors via `WarningsAsErrors`, so a regression breaks the build instead of reaching a node.
When one fires, fix the cause — never `#pragma warning disable` or `[UnconditionalSuppressMessage]`,
which silence the analyzer while the linker still breaks at publish.

**.NET 10 has no Ed25519 in the BCL.** Bundle and request signing use ECDSA P-256 / SHA-256 with
IEEE P-1363 fixed-field signatures, which is in the BCL and AOT-clean. Do not add a third-party
crypto library to the node image for this.

**Key generation shells out to `awg`.** It happens only in the control plane, whose image carries
the binary. Tests substitute `IAwgKeyGenerator`, so the suite runs on machines without it. The 3.x
header protection key is 32 random bytes, which is what `awg genpsk` already produces, so it goes
through the same path rather than a second one.

**The 3.x tools negotiate the netlink encoding; the parameters still need a 3.x module.** The
netlink ABI for H1-H4 changed three times (u32, then a string, then u64), and the 3.x `awg` picks
the encoding from the module's genl family version, so it drives a genl 1, 2 or 3 module. What it
cannot do is make a pre-3.0 module accept `HeaderProtectionKey`, `ContentPaddingAddition`, the
timing ranges, `RandomTrailers` or `DisableCookies`: those are absent from its netlink policy, so
`awg setconf` fails with EINVAL and nothing reaches dmesg. `scripts/install-awg-v3.sh` installs
both halves from the 3.1 line and proves it by applying a real 3.x configuration to a throwaway
interface before it leaves the module in place. The userspace `amneziawg-go` in the node image
speaks the full set, and is the backend a node falls back to.

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
- Every page section is an `AppCard` on the darker canvas, and a section's actions live in its
  own header. Keep an action next to the thing it acts on: switching nodes is a button on the
  node (`NodeCard`), handing out a config is the peer's Config dialog (`PeerConfigModal`). Node
  actions go through `useNodeActions` so the overview graph and the nodes page behave the same.
  `live` is reserved for the path traffic actually takes - the record, the active node and the
  line between them in `FleetGraph`; do not reuse it for emphasis. AmneziaWG parameter names keep
  their exact spelling in mono through `paramField`.
- Everything under `/api` requires an authenticated admin except `/health`, `/auth/*` and
  `/shares/*`. Do not add an endpoint to the anonymous set without a reason worth stating.
- A share link belongs to the person receiving the config, not to the operator. It carries no
  client name - not in the lookup response, not in the config filename - because that label is
  the operator's own bookkeeping about a person, and it is the one thing the link would leak
  that the config itself does not.

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
- Phase 2 (external probes, blocked-vs-down detection, notifications) is designed but not built.
  Failover is manual today: an operator decides, and `DnsFailoverService.ActivateAsync` is what a
  probe would call in its place.
- Only Cloudflare is implemented behind `IDnsRecordUpdater`. Without a token the panel records the
  active node and leaves the record to the operator; a floating-IP provider would be a third
  implementation of the same interface.
- Cloudflare is driven by a name lookup on every switch, so a zone holding several records of the
  same name takes the first. Nothing checks that the zone is the one the endpoint host belongs to.
- A changed egress interface leaves the previous `MASQUERADE` rule behind. The agent installs
  the new one, so traffic flows, but nothing withdraws the old rule until the interface is next
  brought down through `awg-quick`.

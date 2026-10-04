# AGENTS.md

Guidance for coding agents working in this repository.

## What this is

A self-hosted control plane for a fleet of AmneziaWG VPN servers. One panel is the source of
truth; agents on each VPN server converge onto the configuration it publishes. The point of the
fleet design is **seamless failover**: when a node is blocked or dies, traffic moves to another
node without reissuing a single client config.

The project replaced a single-server panel with this architecture. Phase 1 (multi-server with
manual switchover) is complete: the panel picks the active node and moves the DNS record clients
follow, through Cloudflare when a token is configured. Phase 2 is built too: probes check every
node with a real handshake from where clients are, the panel tells blocked from down, and
`FailoverMonitor` moves the record on its own once an operator arms it - through the same call
the button makes.

The predecessor is preserved at tag `v0.9-standalone` and is no longer in the tree. When touching
the legacy import path, that tag is where its `state.json` format is defined.

## Layout

```
src/AwgEasy.Contracts/   Wire contract shared by both sides. Changing it changes the fleet.
src/AwgEasy.Node/        Agent on each VPN server, and the probe (AWG_ROLE=probe). AOT-published.
src/AwgEasy.Control/     Panel: database, admin API, agent API, serves the frontend.
frontend/                Nuxt 4 + Nuxt UI, generated to static files, served by Control.
tests/AwgEasy.Tests/     xUnit. Unit tests plus integration tests hosting the real Control.
docker/                  node.Dockerfile, control.Dockerfile
scripts/install.sh       Node enrollment one-liner, served by Control at /install.sh
```

## Commands

```bash
dotnet build Awg-easy.sln          # whole solution
dotnet test Awg-easy.sln           # 228 tests, all must pass
cd frontend && pnpm run lint       # eslint
cd frontend && pnpm run typecheck  # nuxt typecheck - catches real API/UI type drift
cd frontend && pnpm run generate   # static build into .output/public

dotnet publish src/AwgEasy.Node -c Release -r linux-x64   # the only check of the AOT invariant
```

Run `pnpm run typecheck` after touching anything the frontend consumes. It catches null/undefined
drift between the API and the forms that plain linting does not.

`.github/workflows/ci.yml` runs all of the above on every push and pull request, plus a
`dotnet list package --vulnerable` gate. The publish line matters because the IL trim/AOT codes
are promoted to errors but the analyzer behind them runs on publish and nowhere else - a green
`dotnet build` says nothing about whether the agent still publishes. The images are not built in
CI; that is minutes and gigabytes for something that changes far less often than the code.

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

**Health is judged from two kinds of evidence, and they answer different questions.** The agent's
reports say whether a node runs; probes say whether a client is actually served. A probe counts a
node reachable only when a request through the tunnel is answered, not when the handshake
completes: the block seen in practice lets the first handshake through and drops everything
after it. `ProbeResult.Handshake` keeps the two apart so the reason can say which block it is. The
check leaves through the probe interface by binding its socket to it (`SO_BINDTODEVICE`), with
`Table = off` in the probe config, so a probe never touches its host's routing. A node that
runs and that no probe reaches is `blocked`, which is the state failover exists for and the one a
node can never report about itself. `NodeHealthEvaluator` is the single place that combines them;
keep it pure, so the policy stays testable without a panel. Only current results about the
address the node has now count, and a probe `error` - the probe could not run its own check -
never counts against a node: a broken probe must not be able to move a fleet's traffic.

**A probe is a client, never a node.** It enrolls with a probe token, lives in its own table, is
authenticated against that table, and tunnels with a client row of kind `probe` - a peer on every
node like anyone's client, hidden from the client list. It must never receive a bundle: probes sit
in the networks that do the blocking, and a bundle carries the fleet private key. A node token
cannot enroll a probe and a probe token cannot enroll a node; keep that check in
`EnrollmentService.ReadToken`.

**Automatic failover is reluctant by design.** It is off until armed, cannot be armed without a
DNS provider, and runs the provider's `CheckAsync` before it is armed. It waits out the grace
period, respects the cooldown after any switch, only picks a node that is healthy right now, and
never switches back on its own. Every switch goes through `DnsFailoverService.ActivateAsync`, so
the invariants of the manual switch - no revision bump, active only once the provider confirmed -
hold for it unchanged. The clock that decides staleness is the panel's: status reports and probe
results are stamped on arrival, so a node with a clock running ahead cannot look fresh.

**Grouping peers is the panel's own bookkeeping.** A group is one person; the peers in it are the
devices that person holds. `client_groups`, `clients.group_id` and `clients.sort_order` never
reach a node, never enter a bundle and never bump the revision - the same reason a rename does
not. Deleting a group ungroups its peers and deletes no config, because losing a label must not
disconnect anybody. It is stored server-side rather than in a browser on purpose: the operator
checks the same fleet from a phone and from a desk, and two panels disagreeing about who holds
what is worse than no grouping at all. `ClientRepository.ListEnabled` stays ordered by address
while `List` follows the arrangement, so dragging a peer in the panel cannot change the bytes a
node is handed.

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

**A changed egress interface withdraws the rule the old one needed.** The MASQUERADE rule names
the egress interface, and neither syncconf nor a later `awg-quick down` (which runs the new
config's PostDown) would remove the old one. `AwgInterface` reads which interface the config on
disk masquerades through before overwriting it, and deletes the stale rule once - checked with
`-C` first, since a full restart usually withdrew it already.

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
- A drag starts on a handle, never on the row itself: a row is also a link to a config and a
  menu, and a list that moves when a finger brushes it is worse than one that cannot be
  rearranged. Dragging is off while a search or a filter narrows the list - a position among the
  rows that happen to match is not a position in the list - and every move is also reachable from
  the row's menu, which is what makes it work with a keyboard.
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
- The probe's check - handshake, then a request through the tunnel - has not been run against a
  real node yet; it is covered only through the parser and the panel side of the protocol.
- A probe's assignment is protected by TLS only, not signed like a bundle. It carries no fleet
  secret, and results about an address other than the node's current one are ignored.
- Only Cloudflare is implemented behind `IDnsRecordUpdater`. Without a token the panel records the
  active node and leaves the record to the operator; a floating-IP provider would be a third
  implementation of the same interface.

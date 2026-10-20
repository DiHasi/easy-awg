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
scripts/e2e.sh           The pair actually running: panel, two nodes, a probe, in containers
compose.e2e.yaml         What that script brings up. Not a deployment; the other three are
```

## Commands

```bash
dotnet build Awg-easy.sln          # whole solution
dotnet test Awg-easy.sln           # 339 tests, all must pass
cd frontend && pnpm run lint       # eslint
cd frontend && pnpm run typecheck  # nuxt typecheck - catches real API/UI type drift
cd frontend && pnpm run test       # vitest, in a Nuxt environment
cd frontend && pnpm run generate   # static build into .output/public

sh scripts/e2e.sh                  # the whole pair in containers: builds both images

dotnet publish src/AwgEasy.Node -c Release -r linux-x64   # the only check of the AOT invariant
```

Run `pnpm run typecheck` after touching anything the frontend consumes. It catches null/undefined
drift between the API and the forms that plain linting does not.

`.github/workflows/ci.yml` runs all of the above except `e2e.sh` on every push and pull request,
plus a `dotnet list package --vulnerable` gate. `e2e.sh` has a workflow of its own
(`.github/workflows/e2e.yml`): nightly, on request, and on a pull request that touches the images,
the agent or the contract. It is kept out of the main run because it builds both images, which is
minutes and gigabytes for the half of the system that changes least often - and because what it
covers only breaks when that half changes. The publish line matters because the IL trim/AOT codes
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

**Rotation is the one exception, and it is an act, not a side effect.** `POST /api/fleet/rotate`
exists because the shared identity's cost is its blast radius: a provider that reads one node's
disk has read the fleet private key, every preshared key and the wire format. Only
`FleetRotationService` may replace them, it is reached only from that endpoint, and nothing else
in the panel - no migration, no setting, no upgrade - may move `FleetRecord.Generation`. The whole
change lands in one `FleetRepository.Rotate` transaction (identity, generation, every peer's
preshared key, one revision bump), because a rotation that half-applied is a fleet where nothing
handshakes and the panel cannot say why. Client key pairs are optional and off by default: a node
never held one, so a read node does not expose it, and reissuing them changes every peer's public
key. The bundle signing key pair is **not** rotated - agents pin it at enrollment and refuse
bundles signed by anything else, so replacing it would stop the fleet converging exactly when it
must converge fastest. If you ever need to rotate it, design the re-pinning; half of it is worse
than none.

**Rotation is not containment of the node, and the panel has to say so.** It follows from
fail-static: revoking a node only stops new bundles reaching it, and it goes on re-applying its
cached bundle indefinitely, so the configs issued before the rotation keep working through the
very server whose disk was read. "Revoke, then rotate" therefore is not enough - only powering
that host off is - and an operator has every reason to believe otherwise, which is why
`FleetRotationResponse.Warning` carries it and names the revoked nodes. Do not quietly drop that
field because it reads like a caveat; it is the gap between what rotation does and what the
operator came to do.

**Rotation invalidates every client config, so the panel says who still holds a dead one.**
`clients.config_generation` records the generation a peer's config was last *rendered* at -
stamped by the download, the QR code and the share-link fetch alike, since rendering is the only
moment the panel can observe that somebody was in a position to be handed it. A rotation
deliberately leaves the column alone: making those rows stale is the point. `config_generation` is
the panel's own bookkeeping like grouping - it never reaches a node, never enters a bundle and
never bumps the revision - and it is not a claim that a person installed anything, only that the
panel produced the current config for them. 0 means no config was ever rendered, and that is
**not** stale: nothing of that peer's is in anybody's hands and its own material is current, so
counting it among the people to go and find would be a claim about a config that was never issued.
Share links carry the generation they were made for and resolve only while it is current: the
token travelled over whatever channel was at hand, and after an identity has been replaced because
a server was read, a stale link must not be what hands out the replacement.

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
nonce rides in that padding), `S1` and `S2` may not differ by exactly 56 — that would leave the
padded handshake initiation and the padded response the same length on the wire, which amneziawg
refuses — and `RejectAfterTime` must stay above `RekeyAfterTime`. The legacy import path validates
too, and drops an unusable profile with a warning rather than poisoning the fleet with it. Anything
that *generates* a profile asks the validator for the same rule rather than keeping its own idea of
it: `AwgObfuscationValidator.HandshakeSizesDiffer` is public for exactly that, and
`ServerObfuscationGenerator` draws against it instead of relying on the band its paddings happen to
span.

**The tunnel MTU is one value for both halves, and it is sized against 3.x, not WireGuard.**
`FleetRecord.TunnelMtu` is written into every client config, every probe config, and - unless the
node row overrides it - into the bundle as `NodeSettings.Mtu`. One value because each half must be
sized against the same worst-case client path: two knobs that must agree with nothing enforcing it
is how a fleet half-works. It defaults to 1280 because `awg-quick`'s own route-MTU-minus-80 (1420 on
a 1500 underlay) covers vanilla WireGuard and not the bytes 3.x adds to every transport packet -
`S4`, `ContentPaddingAddition` and `RandomTrailers`, the last on by default under 3.1 - so the outer
datagram crosses 1500, fragments, and dies at any CGNAT that drops fragments. Because the node half
travels in the bundle, changing it **does** bump the revision, unlike the panel's own bookkeeping,
and each node takes its interface down once: `awg syncconf` cannot apply an interface-level setting.
The probe gets the same value deliberately - a probe on a wider MTU could pass a path that fragments
every real client's traffic, and the panel would call that node healthy.

**Switching the active node is not a fleet change.** Failover repoints one DNS record; every
client config already names that host and pins the fleet public key, so the same key, subnet and
obfuscation profile answer at a different address. Nothing is reissued and no node re-applies
anything, so `POST /nodes/{id}/activate` deliberately does not bump the revision. If you ever find
yourself bumping it there, the model has drifted.

**A node below the identity revision is not a failover target, manual or automatic.**
`fleet.identity_revision` is the revision the identity last changed at, written inside
`FleetRepository.Rotate` in the same statement as the revision itself. A node whose
`applied_revision` is below it still answers with the previous key pair, preshared keys and wire
format, so not one config issued since the rotation can complete a handshake with it - while it
reports itself healthy, and in sync with everything except the one thing that matters. So it is a
hard condition in `FailoverPolicy.IsEligible` and, unusually, on the manual path too:
`DnsFailoverService.ActivateAsync` refuses with `node_previous_identity`. Everything else the
policy weighs is a judgement an operator may know better than the panel; this is arithmetic about
which key pair the node is holding, and overruling it disconnects the whole fleet. It resolves
itself within one poll interval, which is why refusing costs nothing. Keep it apart from `InSync`
in the API and in the UI - one peer behind is an inconvenience, an identity behind is a node that
serves nobody - and keep `Stuck` able to say which of the two it hit, or the operator goes looking
for a fault that is not there.

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

**The grace period has to outlast a probe round, and since rotation that is a requirement rather
than a coincidence.** A probe re-renders its config only when it fetches its next assignment, so
for up to one `AWG_PROBE_INTERVAL_SECONDS` after a rotation every probe is still speaking the
previous wire format with the previous preshared key: no handshake completes anywhere and the whole
fleet reads as `blocked`, including the nodes that have already converged. The default 120s grace
against a 60s probe interval covers it, so nothing switches - but `AWG_FAILOVER_GRACE_SECONDS` must
stay above `AWG_PROBE_INTERVAL_SECONDS` or a rotation becomes a reason to fail over. `FailoverMonitor`
also withholds newly-failing transitions for one probe round after `identity_rotated_at`, without
recording them, so the rotation does not page an operator about the fleet they are standing over
and a block that is still there afterwards is still announced. Probe rounds inside that window do
land in `probe_availability` as failures; that is a known gap, not a decision.

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

**Traffic history is a difference, and the reading it is a difference from is the row being
overwritten.** A node reports totals, so what a peer moved is the gap between two status reports.
`UsageAccounting.Accrue` therefore runs inside `NodeRepository.ReplacePeerStats`' transaction,
before the delete: splitting them would lose an interval or count it twice whenever the panel
restarted in between. A reading below the one before it means the device started counting again -
a reboot, an interface brought down - so all of it is traffic since that restart, the same rule
`SinceReset` applies to the lifetime totals. A peer with no previous reading is credited nothing
rather than its whole counter, which may have been climbing since before this panel existed.
Manual counter resets are deliberately not applied here: a reset changes what the lifetime number
shows, not what happened last Tuesday.

**The traffic history is the panel's own bookkeeping, like grouping.** `client_usage` never
reaches a node, never enters a bundle and never bumps the revision. It is keyed by the client
rather than by the public key because it is a record about a person: deleting a peer deletes its
history in the same request, and `UsageRepository.Prune` sweeps rows whose client is gone for the
paths that delete wholesale. `AWG_USAGE_RETENTION_DAYS` is one knob on purpose - an hourly record
of what each person moved is considerably more than the lifetime counter beside it, and `0` has to
mean "record none and drop what was recorded", not "hide the page". The live counters come from
the nodes, so they keep working either way.

**The availability history is the panel's own bookkeeping, like grouping and traffic.**
`node_availability` and `probe_availability` never reach a node, never enter a bundle and never
bump the revision. Two things about them are load-bearing. They count **samples**, not seconds:
the round interval is a setting, so an operator who halves `AWG_FAILOVER_CHECK_SECONDS` would
otherwise halve every hour recorded after the change, while a share of samples reads the same
either way. And a bucket with **zero samples is "no evidence", not "not healthy"** - it is the
panel having been restarted - so anything that divides by samples has to keep that apart, or an
upgrade reads as an outage. A revoked node records nothing: the panel cut it off on purpose, and
recording that as unreachable would be the panel measuring its own decision. They are two tables
rather than one because the vocabularies genuinely differ - a node is healthy, blocked, down or
silent; a probe got through, did not, or could not run its check - and the per-probe half is the
only place the panel can say *where* a node is blocked. Retention is a knob of its own,
`AWG_AVAILABILITY_RETENTION_DAYS`: `AWG_USAGE_RETENTION_DAYS` is a decision about holding a record
of people, and it has nothing to say about whether the panel may remember which of the operator's
own nodes was blocked last week.

**The accrual sits where the evidence is produced.** A node's sample is written by
`FailoverMonitor` in the round that assessed it, so `NodeHealthEvaluator` stays a pure function of
its evidence. A probe's is written by `AvailabilityRepository.AccrueProbeRound` inside
`ProbeRepository.RecordRound`'s transaction, for the same reason `UsageAccounting.Accrue` runs
inside `ReplacePeerStats`: a round is reported once, and the latest-result row it also writes is
about to be overwritten by the next one, so a second pass over those rows would miss rounds.

**The metrics endpoint exports servers, never people.** `/api/metrics` is off until
`AWG_METRICS_TOKEN` is set, and it is the one route under `/api` that authenticates with something
other than the admin cookie - it checks that token itself, in fixed time. What it exposes is
deliberately bounded: no client names, no public keys, no per-peer series, and no node addresses.
A scrape is federated, backed up and shared far more casually than this database, so a peer label
would put "who" into all of it, and a fleet's addresses are what a blocker wants. Traffic is
exported per node, which is the operational question; the Traffic page keeps the other one. Node
counters are exported raw, without the panel's reset baselines, because a scraper is the one
reader that handles a counter going backwards and subtracting there would hide a node reboot from
it. There is a test asserting the absence of a client name, a key and an address in the output;
do not weaken it.

**Per-node settings are validated before they can reach a node.** `EgressInterface` and `Mtu` are
the per-node half of the bundle, and a node applies its bundle in one shot - so a value `awg` or
`iptables` refuses does not produce a complaint, it produces a node whose interface does not come
up at all while the panel shows it merely out of sync. `NodeOverrides.TryRead` is the one place
both paths go through: the token that carries them to a new node, and `PUT /nodes/{id}` for one
that already exists. A blank field means "the fleet value applies", never an empty string. The two
bundle-visible fields bump the revision, because the bundle is served against an ETag of the
revision and an unchanged revision is answered with a 304 - the node this is about would never
hear. The name and the endpoint host do not, like a client rename.

**Both histories are read over the same windows.** `HistoryWindows` owns the window set, the range
arithmetic and the bucket SQL for the traffic history and the availability history alike. Two sets
that drifted apart would make the same button mean different spans on two pages. Error codes stay
per-page (`usage_window_invalid`, `availability_window_invalid`) because a code is part of the API
and sharing the arithmetic must not rename one.

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
- Everything under `/api` requires an authenticated admin except `/health`, `/auth/*`,
  `/shares/*` and `/metrics` - the last checks `AWG_METRICS_TOKEN` itself and is off without one.
  Do not add an endpoint to the anonymous set without a reason worth stating.
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

- The pair has only been exercised in containers on one host, never across real servers. What
  `scripts/e2e.sh` covers there is real, though: agents applying bundles with awg-quick, a probe
  completing handshakes and fetching through the tunnel, and both kinds of block.
- The frontend's tests cover the formatters, the node state words and the availability ribbon.
  The composables that need a Nuxt app and a stubbed API - `usePeerArrangement`, `useNodeActions` -
  are still only exercised by hand.
- A probe's assignment is protected by TLS only, not signed like a bundle. It carries no fleet
  secret, and results about an address other than the node's current one are ignored.
- Only Cloudflare is implemented behind `IDnsRecordUpdater`, by choice. Without a token the panel
  records the active node and leaves the record to the operator. Another provider - a different DNS
  API, or a floating IP - is a second implementation of that interface and nothing else.
- Automatic failover cannot be exercised end to end in `e2e.sh`: arming it requires a DNS provider,
  and there is none in CI. The script asserts the recommendation the manual path produces instead,
  which goes through the same policy.

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
src/AwgEasy.Control/Ha/  The panel pair: replication, the term, promotion. Panel to panel only.
src/AwgEasy.Control/Demo/ The read-only demo account: the mask, and what it refuses.
frontend/                Nuxt 4 + Nuxt UI, generated to static files, served by Control.
tests/AwgEasy.Tests/     xUnit. Unit tests plus integration tests hosting the real Control.
docker/                  node.Dockerfile, control.Dockerfile
scripts/install.sh       Node enrollment one-liner, served by Control at /install.sh
scripts/e2e.sh           The pair actually running: panel, two nodes, a probe, in containers
compose.e2e.yaml         What that script brings up. Not a deployment; the other three are
docs/                    Operator documentation, en/ and ru/ in step. README.md is the overview
```

## Commands

```bash
dotnet build Awg-easy.sln          # whole solution
dotnet test Awg-easy.sln           # 466 tests, all must pass
cd frontend && pnpm run lint       # eslint
cd frontend && pnpm run typecheck  # nuxt typecheck - catches real API/UI type drift
cd frontend && pnpm run test       # vitest, in a Nuxt environment
cd frontend && pnpm run generate   # static build into .output/public

sh scripts/e2e.sh                  # the whole pair in containers: builds both images

dotnet publish src/AwgEasy.Node -c Release -r linux-x64   # the only check of the AOT invariant
```

Run `pnpm run typecheck` after touching anything the frontend consumes. It catches null/undefined
drift between the API and the forms that plain linting does not.

`.github/workflows/ci.yml` runs all of the above except `e2e.sh` on every pull request and on every
push to `main`,
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
stay above `AWG_PROBE_INTERVAL_SECONDS` or a rotation becomes a reason to fail over. `RotationSettling.IsSettling`
is that window, and it is one rule with three readers: `FailoverMonitor` withholds newly-failing
transitions inside it, without recording them, so the rotation does not page an operator about the
fleet they are standing over and a block still there afterwards is still announced; the node sample
for that round is not written; and `ProbeRepository.RecordRound` does not accrue the probe round
either. A minute the panel itself made unobservable is therefore recorded as no evidence rather
than as a fleet-wide block - and nothing is written in place of it, because a window filtered down
to the rounds that came out well would claim an uptime the evidence does not support. The latest
probe results are still stored: the page shows what probes actually see, and the grace period above
is what keeps anything from being done about it.

**The panel is a pair, and which half this one is lives in the database.** A standby is a second
Control with a database of its own that replicates the first and refuses every write. Its role is
in `panel_local`, the one table a snapshot never replaces, and never in the environment: a
promotion has to survive a restart, or an operator who promoted the standby at three in the
morning finds the container has put it back. `AWG_PANEL_ROLE` seeds that row on an empty database
and is ignored afterwards. The role has to stay out of the replicated set in the other direction
too - a standby that replicated the active's role would read itself as active the moment it first
synced, and a promotion that lived there would be undone by the next snapshot from a panel that
has not heard about it yet.

**A standby replicates what the fleet runs on and what the panel observed, and not the event
log.** Two payloads, apart because they change on different clocks. The snapshot is the
configuration - identity, clients with their private keys, groups, arrangement,
`config_generation`, each node's own settings, probes, admins, enrollment tokens, share links,
counter baselines - pulled whole and gated by an ETag, so an unchanged fleet costs one request.
The stream is the evidence: the columns the agents report, `peer_stats`, `probe_results` and the
panel rows, replaced wholesale, plus `client_usage`, `node_availability` and `probe_availability`
read from a cursor so a cold standby catches up over several polls rather than in one enormous
response. The split follows the same line the rest of the panel already draws, and it is why a
node's `status` is not in the snapshot: an ETag that moved every twenty seconds would transfer the
whole fleet on every poll. `events` is deliberately in neither. It is each panel's own account of
what was done on it, its key is a local autoincrement that two writers cannot share without
renumbering one of them, and a panel that takes over has its own story to tell from the moment it
did - which the promotion writes into it.

**Rows travel as rows.** The obfuscation profile crosses as the JSON the column holds, not as a
reparsed object, so what a standby stores is byte for byte what the active stores. A fleet's wire
format must not be able to drift because two panels serialized it differently - the same reason
`ContractsJsonContext` is mandatory on both sides of a bundle.

**The term is the whole of the fencing, and it is arithmetic rather than a judgement.**
`fleet.panel_term` is monotonic and moves once per promotion, in the same statement that names the
panel holding it. The pair watches itself: every round the panel in charge sends one signed hello
to each peer, and a peer answering at a higher term means this panel has been superseded, so it
steps down at once - no quorum, no arbitration, last promotion wins. That is also why
`AWG_PANEL_URL` is required before a standby can be enrolled: a panel nobody can reach can never
be told. Two panels at the same term is the one case the term cannot settle, and nothing is done
automatically - the operator is told which two to look at, because picking a winner by coin toss
is worse than saying so.

**The panel that created the fleet pins its own signing key.** Everything a panel accepts from a
peer is verified against a key pinned at enrollment - but the first panel never enrolls into
anything, so it has none, and a standby without one can never accept a snapshot. That made
stepping down a one-way door: the first panel of every pair could be superseded and then never
replicate, never catch up and never come back. `PinOwnSigningKey` takes the key out of its own
fleet row, which is the very key the other panel signs with, because the signing key pair is what
a rotation deliberately leaves alone. It is pinned when the pair is formed and, for a pair formed
before this existed, on the way down - and it never overwrites a key pinned at enrollment, which
is the only one that proves anything about where the state came from.

**Whoever answers a hello has to be the panel that was asked.** `hello.PanelId` is checked against
the peer the request was addressed to, because the configuration that breaks the pair is the one
an operator reaches for first: giving a panel the moving record as its own `AWG_PANEL_URL`. After
a switch that name resolves to whichever panel is in charge, so the hello comes back from the
calling panel itself - carrying its own id and its own term, which clears the term comparison, the
split-brain check and everything else, and writes a reassuring lie into the peer's row. Each panel
needs a name of its own that never moves; the record agents follow is a different name.

**A panel renames itself only when it is told a name.** `RecordSelf` writes `name` through a
COALESCE, so `AWG_PANEL_NAME` renames and its absence leaves the stored name alone. The fallback
to the machine name belongs to creating the row and nowhere else: a standby is named by the
operator in the enroll dialog, and in a container the fallback is the container id - a new one
every time it is recreated. Rewriting the column from it threw away the only name anybody chose.

**A panel with no fleet state is never promoted.** `POST /api/panel/promote` refuses with
`panel_never_synced`, and `PanelFailoverPolicy` refuses the same thing however armed and however
long the silence. A panel promoted without a snapshot has an empty clients table, and the first
thing it would do is serve every node a bundle with no peers in it - which every node would
apply, because that is what a node is for. The whole fleet would stop serving traffic within one
poll interval and the panel would report it as a successful switch. It is the one refusal here
with no override.

**A standby is read-only, and the rule lives in one place.** `PanelWriteGuard` is the whole of it,
for the same reason `AdminApi` keeps the authorization boundary in one short method: a rule spread
across forty endpoints is one that will be forgotten the next time an endpoint is added. It
refuses every write to the fleet - addresses are allocated by one panel only, because they must be
unique fleet-wide, and two panels allocating out of one subnet is the fleet breaking silently -
the agent and probe APIs entirely, and, although they are GETs, handing out a client config or
resolving a share link, because rendering one stamps `config_generation` and a stamp written on a
standby never reaches the panel in charge. It answers 503 rather than 403: the caller is allowed
to ask, just not here, and 503 is what lets a node treat it as transient and stay fail-static
instead of giving up on its identity.

**The demo account reads the fleet without being trusted with it, and both halves of that are
one place each.** `DemoGuard` is the refusal and `DemoMask` is the masking, for the same reason
`PanelWriteGuard` is one file: a rule spread across forty endpoints is one that will be forgotten
the next time an endpoint is added - and here what would be forgotten is not a refusal, which
announces itself, but a leak, which does not. So the refusal is a rule about the method rather
than a list of write endpoints, and the masking is a pass over the response body rather than a
mapping per endpoint. Both are deliberately blunt: an endpoint added later is refused if it writes
and masked if it reads, without being named anywhere. Do not "fix" that by moving either decision
into the endpoints.

**The mask is built from the panel's own tables, and that is what makes it total.** Every client,
group, node, probe, panel and operator name, every address, every public key, the subnet and the
endpoint host are mapped to a stand-in, and the map is applied to every string in a response
whatever field it sits in - so a response shape nobody thought about still comes out masked. The
field table (`publicIp`, `hostname`, `resolvedAddresses`, ...) is the second layer, for values the
panel does not hold: whatever a resolver answered, an error string from a node. `Untouched` is the
third and is not an oversight - ids, event kinds, status words and routing constants are not data
about anybody, and substituting them breaks the panel without protecting anyone: an operator
called `admin` must not have `admin.login` rewritten into a code no page knows, and a demo that
showed a documentation address in `AllowedIPs` would be showing a config that routes nothing.

**A stand-in is derived, not drawn.** Everything goes through an HMAC over a key drawn once per
process, so one peer is one person on every page, in every graph and inside every sentence the
panel wrote about it - a demo where each page invented its own names is noise, not a demo - and
nobody holding the demo can run the derivation backwards. The key is never stored: nothing needs a
pseudonym to outlive a restart, and a secret that is never written down cannot be read off a disk.
Addresses and names land in the ranges reserved for documentation, because a plausible address
drawn at random belongs to somebody and a demo must not invite a visitor to go and scan it.

**The demo's own wire format is drawn, and it is the must-match half only.** S1-S4, H1-H4 and the
header protection key are what makes this fleet's packets recognizable as this fleet's - the very
thing `FleetRotationService` replaces - so publishing them to whoever is looking at a demo hands a
censor the one piece they are short of. The may-differ half comes through untouched, because it
differs per peer by design and therefore describes nobody. The stand-in keeps the fleet's shape: a
padding that is unset stays unset and a single message type stays single, because that shape is
what decides whether a node needs 3.x and the demo must not contradict the badge beside it.

**The one response the mask does not rewrite is rendered masked instead.** A client config is not
JSON, so `DemoMask.RenderConfig` hands `ClientConfigRenderer` a peer and a fleet that are already
nobody's rather than scrubbing the output line by line. That is the stronger construction, and it
is also why the demo path stamps nothing: rendering records that a peer was in a position to be
handed its current config, and a visitor shown a stand-in was handed nothing - a stamp written for
them would send an operator chasing somebody who is holding exactly what they were before.

**The demo flag lives in the database and replicates.** `admins.is_demo`, carried in the snapshot
beside the password hash, because an account's privileges are part of what the fleet runs on: a
promoted standby that read the demo account as a full one would hand it the fleet. It is read per
request rather than written into the session cookie, so an account made read-only is read-only on
its next request rather than in twelve hours. `AWG_DEMO_USER` creates the account and never
changes one that exists - making an operator's own account read-only could lock them out, and
putting a full admin behind a password published next to a demo link is the failure the whole
thing exists to avoid.

**Automatic panel promotion is weaker than node failover, and the difference is the witness.** A
node has probes in other networks to vouch for it. When a standby cannot reach the panel in charge
it has learned exactly one thing - that it cannot reach it - and is in no position to say which of
the two is the one that is cut off. So the only evidence available is whether its own uplink still
carries packets (`AWG_PANEL_WITNESS_URLS`, checked only once the grace period has elapsed, so a
healthy pair makes no such requests); arming is refused without one; and the grace period must
exceed the pull interval or the panel will not start. Even then this reduces the chance of being
wrong rather than removing it, and the term is what makes being wrong survivable. There is also a
local brake, `panel_local.hold`, reachable on a read-only panel precisely because a brake that
needs the other panel to be reachable is not a brake.

**Promotion does not wait for the record, which is the opposite of what node activation does.**
`DnsFailoverService` refuses to mark a node active when the record did not move, because "active"
there means "this is where client traffic goes", and a panel that claimed otherwise would be lying
about the one fact an operator checks under pressure. Here it means "this panel is in charge of the
fleet", which is true the moment the term moves - and a panel that refused to take over because a
DNS API was unreachable would be useless in exactly the situation it exists for. So it takes over,
and says plainly what still has to be done by hand. That is safe because a superseded panel
refuses every agent call and nodes are fail-static: a record that has not caught up costs the
fleet its management for a while, never its data plane.

**Everything a panel accepts from another panel is signed, bound, expiring, and refused when it is
behind.** Signed with the fleet signing key and verified against the one pinned at enrollment -
the same arrangement an agent has, and another reason the signing key is never rotated.
`PanelGuard` adds the binding, the expiry and the schema check; the term check is separate and is
the one that matters for a pair, because a panel that lost a promotion still holds the signing key
and can still produce payloads that verify perfectly, carrying the fleet as it was before it lost.
The snapshot's ETag is taken over the payload with its stamps cleared - hashing a stamped envelope
would make every poll a change, and every poll a full transfer.

**A history cursor is cut on a whole timestamp.** `updated_at` is not unique: one accrual writes a
row per peer with the same stamp. A batch that stopped inside such a group would either lose the
rest of it, if the cursor advanced, or never get past it, if it did not. So the active takes every
row up to and including the stamp of the row that would have been last, and the standby resumes
strictly after it.

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

**Clearing the availability history is an operator act, and it has to be, because the record can be
wrong rather than merely old.** `AvailabilityRepository.Clear` drops one node's rows or every row,
and `POST /nodes/availability/reset` and `POST /nodes/{id}/availability/reset` are the only way in.
The standing reason is the grace period above: for one probe round after a rotation no handshake
completes anywhere, so the whole fleet is recorded as blocked while nothing was wrong with any of
it, and a bucket holds counts rather than the rounds behind them - there is nothing to correct in
place. Deleting rather than hiding, for the same reason the zero-sample bucket exists: a row kept
out of sight is still a number every share would have to decide whether to divide by. It is panel
bookkeeping like the rest of the history, so it bumps no revision, reaches no node, and leaves the
live states alone - they are judged from current evidence, which is why clearing costs the panel
nothing it needs in order to act.

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

**A peer picks a label; a node is handed an address.** One server often carries several public
addresses, and they are not interchangeable: the services behind one of them have filed it as
belonging to a region it is not in, or into a shadow ban, so one group of people wants that address
and another wants anything else. `clients.egress_label` is therefore a name, resolved per node by
`FleetService.BuildBundle` into `BundlePeer.EgressAddress`, with `nodes.egress_plan` holding what
that node calls each of its addresses and which one is its default (`NodeSettings.DefaultEgressAddress`).
An address would have been simpler and is wrong: addresses belong to one node, so a peer pinned to
one would silently lose it the moment failover moved traffic - which is the thing this whole
architecture exists to make invisible. A label a node has no address for resolves to null and that
peer leaves through the node default, deliberately. The labels themselves never cross the wire, for
the same reason address allocation belongs to the panel, and a change to either half bumps the
revision because the bundle is served against an ETag of it.

**Which addresses a node carries is a thing only the node can answer.** The agent enumerates them
off the egress interface and reports them in `NodeStatusReport.EgressAddresses`, with the address
the kernel is currently picking marked and what an echo service saw for each one beside it - the
last being the whole question an operator is asking when they pin anybody. So the panel offers what
was reported rather than a field to type an address into, and a round that could not enumerate
reports **null** rather than an empty list: `RecordStatus` COALESCEs it, so a failed lookup does not
empty the dialog an operator is in the middle of. A status-report field is not a bundle field, so
none of this needs a synchronized upgrade.

**The egress rules live in a chain of the agent's own, and are rebuilt rather than appended to.**
`NodeFirewall.EgressChain` holds one `SNAT --to-source` per pinned peer and the node default last;
`AwgInterface` compares it against `iptables -t nat -S` and flushes and refills on a mismatch. These
are the only rules here that go stale on their own - a pin disappears when its peer is deleted or
re-labelled - and the check-then-append loop that keeps the other rules alive can never notice a
rule that ought to be gone. The order inside the chain is load-bearing, since SNAT terminates: the
per-peer rules have to be seen before the default that covers the whole subnet. The jump into the
chain is **inserted** at POSTROUTING 1 rather than appended, because on an upgrade the blanket
MASQUERADE is already installed and a jump below it would leave every pin silently doing nothing.
That MASQUERADE stays in the hooks: a node that comes up without its agent still NATs, so an
unapplied pin costs a peer the address it was meant to leave with and never its traffic.

**An address the node does not carry is skipped, not installed.** SNAT to a non-local source leaves
that peer with no working traffic at all, where leaving the rule out leaves it on the default. The
panel holds both halves of the discrepancy - the plan it published and the addresses the node
reports - so it can say who is affected without the node inventing an error field. A node that
could not read its addresses applies what the panel published: null is no evidence, not evidence of
nothing.

**The schema that carries the 3.x wire format is not the newest schema.**
`BundleSchema.Schema3ObfuscationVersion` is 2 and `DesiredStateBundle.CurrentSchemaVersion` is 3.
`FleetService.BuildBundle` downgrades the obfuscation profile below the first and omits the egress
fields below the second, and `FailoverPolicy.IsEligible` asks about the first. They were one
comparison while the two numbers were equal, and the bump that added the egress fields would
otherwise have served every node one release behind a downgraded profile its own clients cannot
handshake with - and taken it out of the failover rotation at the same time. Keep them apart.

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
- `README.md` is an overview and stays one: the operator documentation lives in `docs/`, as
  `docs/en/<page>.md` and `docs/ru/<page>.md`. The two languages are the same pages under the same
  file names, and a change to one is made to the other in the same commit - the language switch at
  the top of every page promises a reader that the other side says the same thing. Panel labels
  (`Nodes → Enroll node`, `Settings → Tunnel MTU`) stay in English in both, because that is what
  is on the screen.
- Comments explain **why**, not what. Match the surrounding density; do not narrate obvious code.
- Endpoints return `ApiError { code, message }` on failure; codes are snake_case and stable.
- Timestamps are stored in SQLite as ISO-8601 round-trip strings so the file stays inspectable.
- No ORM. Hand-written SQL via `Microsoft.Data.Sqlite` and the helpers in `SqliteExtensions`.
- Frontend: Nuxt UI components, semantic Tailwind tokens (`text-muted`, `border-default`, …).
  The API models "unset" as `null`, form inputs need `undefined` — convert at the boundary.
- A select item may never carry an empty value. The component underneath reserves the empty
  string for its own placeholder and *throws* on an item that uses one, so a "none of these"
  entry never renders - and a field that had been set could then never be unset again, which is
  how a pinned peer became impossible to put back on its node's default. Unset is the
  placeholder, and clearing it is a button beside the field. `egressChoices` has the test.
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
- The demo account conceals who, not what. A visitor still learns how many peers there are, how
  many nodes, how often they are blocked and in whose network, and how much traffic goes through
  them. On a small fleet that is not nothing, and no amount of masking changes it - the answer for
  somebody who cannot publish that is a seeded panel, not this.
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
- A panel pair has only been exercised in the integration suite, where both panels are hosted in
  one process and talk over each other's in-memory servers. What that covers is the real protocol
  - signed requests, signed payloads, the ETag gate, the term, the write guard, and an agent
  accepting a bundle from the panel it did not enroll with - but it is not two machines, and
  `e2e.sh` does not bring up a second panel.
- Automatic panel promotion is covered only through `PanelFailoverPolicy`, which is pure. Driving
  it end to end needs a grace period that has actually elapsed and a witness that answers, and a
  suite that reached the internet to decide a test would be worse than the gap.
- Each panel prunes its own copy of the two histories, so `AWG_USAGE_RETENTION_DAYS` and
  `AWG_AVAILABILITY_RETENTION_DAYS` have to match on both. A standby set lower drops rows its
  cursor has already passed and will not fetch again.
- Clearing the availability history is local to the panel it is run on, for the same reason: the
  stream carries rows forwards from a cursor and has no way to say that a row is gone, so a standby
  keeps what it already replicated and brings it back if it is promoted. A standby cannot be
  cleared while it is one - `PanelWriteGuard` refuses the write - so the repair there is to clear
  it again once it is in charge.
- `/api/metrics` answers on either panel, from that panel's own copy of the data, so scraping both
  double-counts every series. Scrape one, or the panel record.
- Nothing verifies that a node's egress addresses still work. A probe checks the address clients
  connect *to*, so an address the provider has stopped routing outbound leaves the node healthy
  while the peers pinned to it reach nothing. The per-address observation in the status report is
  the closest thing - what that address looked like from outside the last time the node asked -
  and it is a diagnostic, not a check. A probe per egress address would be the real answer and is
  a much larger thing: a probe's config is rendered by the panel and tied to one node, not to one
  source address on it.
- `e2e.sh` can cover a node's default egress address but not a per-peer pin: there is no client
  container to pin, and the probe's peer is not reachable through the client endpoints by design.
  What it asserts is the chain's contents and that traffic through the tunnel moves the default
  rule's counter.
- Egress addresses are IPv4 only, like the rest of the tunnel, and a changed pin applies to new
  connections: conntrack keeps the translation of the ones already established. Flushing it per
  peer would make a change immediate at the cost of dropping that person's live connections, and
  `conntrack` is not in the node image.

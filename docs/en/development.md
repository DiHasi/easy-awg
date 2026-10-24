[← Documentation](../README.md) · **English** · [Русский](../ru/development.md)

# Development

```bash
dotnet build Awg-easy.sln
dotnet test Awg-easy.sln

cd frontend
pnpm install
pnpm run dev        # against a locally running control plane
pnpm run lint
pnpm run typecheck
pnpm run test       # vitest, in a Nuxt environment
cd ..

# The agent is published ahead of time (AOT), and the trim/AOT analyzer runs only here - a
# green `dotnet build` says nothing about whether the agent still publishes.
dotnet publish src/AwgEasy.Node -c Release -r linux-x64
```

`.github/workflows/ci.yml` runs all of that on every pull request and on every push to `main`, and
fails on a known advisory anywhere in the dependency graph. The container images are not built there: they pull a
Go toolchain and the AmneziaWG sources, which is minutes and gigabytes for something that changes
far less often than the code.

## Running the whole pair

```bash
sh scripts/e2e.sh     # needs docker, curl, jq and /dev/net/tun
```

This brings up a panel, two nodes and a probe in containers on one host and drives them through
the admin API. It covers what the test suite cannot reach: an agent applying a bundle with
awg-quick and amneziawg-go, a probe completing a real handshake with each node and fetching a
page through the tunnel, and both kinds of block - a handshake that never completes, and one that
completes and then carries nothing. It builds both images, so it runs in a workflow of its own
(`.github/workflows/e2e.yml`): nightly, on request, and on a pull request that touches the images,
the agent or the contract.

Which project does what is in the [layout table](../../README.md#layout).

## Inspecting a bundle without a panel

Useful when debugging a node:

```bash
awg-node --render-bundle bundle.json --control-key <base64url> --egress ens3
```

It runs the acceptance checks and prints the interface config it would apply, without touching
anything. Both flags are optional: `--egress` falls back to `eth0`, and without `--control-key` the
signature is **not** verified — it says so on stderr and renders the payload as it stands, which is
fine for reading a bundle and no basis for trusting one.

See [AGENTS.md](../../AGENTS.md) for the architectural invariants worth knowing before changing code.

---

[← Configuration reference](configuration.md) · [Documentation](../README.md) · [Roadmap and known gaps →](roadmap.md)

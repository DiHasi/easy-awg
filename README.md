# AWG Easy

A self-hosted control plane for a fleet of AmneziaWG VPN servers.

One panel is the source of truth for clients, keys and obfuscation settings. Agents on each VPN
server pull that configuration and converge onto it. The point of the fleet design is **seamless
failover**: when a server is blocked or dies, you move traffic to another one without reissuing a
single client config.

> Status: the fleet architecture is in place with **manual switchover** - one button in the panel
> moves the DNS record to another node. Automatic failover, external probes and notifications are
> designed but not yet built. See [Roadmap](#roadmap).

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
# the panel; leave it out and the agent identifies itself as 0.0.0-dev.
docker build -f docker/control.Dockerfile -t dihasi/awg-control:latest .
docker build -f docker/node.Dockerfile   -t dihasi/awg-node:latest --build-arg AGENT_VERSION=1.3.1 .
docker push dihasi/awg-control:latest && docker push dihasi/awg-node:latest
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

In the panel: **Nodes → Add node**. You get a ready-to-run command:

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

## Switching which node clients use

Every client config names one host and pins the fleet's server public key. Failover is therefore
nothing more than making that host resolve to a different node: same key, same subnet, same
obfuscation profile, different server. Nothing is reissued, no node re-applies anything, and the
fleet revision deliberately does not move.

In the panel: **Nodes → Make active** on the node you want clients on. Each node shows the public
address its own agent discovered, which is what the record is pointed at, and the card at the top
of the page shows the record, its TTL, and whether the panel's own resolver already agrees.

With `AWG_CLOUDFLARE_API_TOKEN` and `AWG_CLOUDFLARE_ZONE_ID` set, the panel edits the record
itself - an A or AAAA record, never proxied, since the orange cloud carries HTTP only and would
swallow the tunnel. The token needs `Zone:DNS:Edit` on that zone and nothing else. If the API
refuses, the switch is refused with it: the panel never claims a node is active while the record
still points elsewhere.

Without a token the button still works - it records which node is active and shows the record to
set - and the resolver check is what confirms you made the change.

Clients move over as resolvers expire the answer they were handed, so the record's TTL is the real
speed limit. A node does not need to be reachable for this: switching away from a dead node is
exactly the case it is for.

If a node reports no address at all, its agent could not reach any echo service. Set `AWG_PUBLIC_IP`
on that node to state it directly.

## Recovering a node

Revoking or deleting a node in the panel cuts off its configuration, but it does **not** stop the
tunnel: the agent keeps serving traffic from its cached bundle. That is the fail-static design
working, not a bug.

To bring such a server back, issue a fresh enrollment token and re-run the installer. If the old
node still exists in the panel, delete it first — the agent keeps one key pair for its lifetime,
and the panel refuses to register the same server twice.

If the node's own identity is what you want to discard — a lost or suspect server — remove its
state and let it enroll from scratch:

```bash
docker rm -f awg-node && rm -rf /etc/awg-node/*
```

## Migrating from a single-server deployment

Existing users are not disconnected, because the import preserves the original server key pair.

1. Export the backup from the old server: `GET /api/backups/export`.
2. In the panel: **Fleet → Import**, enable *Replace existing clients*, upload the file.
3. Check that **Server public key** on the Fleet page matches the old one.
4. Replace the old container on that server with the agent (the install command above).
5. The agent applies the configuration with `awg syncconf`, so the interface is never brought
   down and existing tunnels survive - unless the subnet or the MTU differs from what the
   interface is already carrying, which no in-place sync can change. Then it restarts the
   interface once and says so in its log.

For a fresh panel you can instead point `AWG_IMPORT_LEGACY_STATE` at a mounted `state.json`.

## Configuration

### Control plane

| Variable | Purpose |
| --- | --- |
| `AWG_ENDPOINT_HOST` | What clients use as the endpoint. **This is the failover switch.** |
| `AWG_PORT` | UDP port clients connect to. |
| `AWG_SUBNET` | Tunnel subnet; addresses are allocated fleet-wide from it. |
| `AWG_CLIENT_ALLOWED_IPS` | `AllowedIPs` written into client configs. |
| `AWG_CLIENT_DNS` | DNS servers written into client configs. |
| `AWG_ADMIN_USER`, `AWG_ADMIN_PASSWORD` | Creates the first admin on an empty database. |
| `AWG_CONTROL_DB` | SQLite file. Default `/etc/awg-control/control.db`. |
| `AWG_BUNDLE_LIFETIME_MINUTES` | How long a signed bundle stays valid. Default 15. |
| `AWG_IMPORT_LEGACY_STATE` | One-shot adoption of an old `state.json`. |
| `AWG_CLOUDFLARE_API_TOKEN` | Lets the panel move the record itself. Needs `Zone:DNS:Edit`. |
| `AWG_CLOUDFLARE_ZONE_ID` | The zone the record lives in. Both are required, or switching stays manual. |
| `AWG_DNS_RECORD_NAME` | Record to move. Defaults to `AWG_ENDPOINT_HOST`. |
| `AWG_DNS_TTL` | TTL written with the record, and the speed limit on failover. Default 60. |

### Node

| Variable | Purpose |
| --- | --- |
| `AWG_CONTROL_URL` | Where the panel lives. |
| `AWG_ENROLLMENT_TOKEN` | Only needed for the first start. |
| `AWG_EGRESS_INTERFACE` | Leave empty to detect the default-route interface. |
| `AWG_HEALTH_URL` | Agent health endpoint. Binds on the host. Default `http://127.0.0.1:8081`. |
| `AWG_POLL_INTERVAL_SECONDS` | How often to check for a new revision. Default 20. |
| `AWG_NODE_STATE_PATH` | Agent identity and cached bundle. Default `/etc/awg-node`. |
| `AWG_INTERFACE` | Interface name. Default `awg0`. |
| `AWG_PUBLIC_IP` | States the node's public address instead of discovering it. |
| `AWG_PUBLIC_IP_URLS` | Echo services used to discover it. Empty switches discovery off. |
| `AWG_PUBLIC_IP_REFRESH_MINUTES` | How often a known address is checked again. Default 10. |

Obfuscation is **not** configured through environment variables. It is fleet-wide and lives in the
panel under **Fleet**, so a change applies to every node at once.

## Obfuscation (AmneziaWG 3.x)

The panel targets the AmneziaWG 3.1 line. 3.x exists because masking individual traits stopped being
enough: a tunnel with randomized packet sizes but a fixed rekey interval and a fixed keepalive
cadence is still recognizable. So most of the new settings are *ranges* rather than values, written
as `lo-hi`, and the peer re-rolls within the range on every use. A single number still works, and
`0`, `0-0` or `off` means "behave like stock WireGuard".

The settings divide in two, and which half a setting is in decides where you configure it.

**Must match on both ends** - fleet-wide, written into every node interface and every client config:

| Setting | Notes |
| --- | --- |
| `S1`-`S4` | Random padding on init, response, cookie and transport messages. |
| `H1`-`H4` | Message type identifiers. Now ranges; the four may not overlap, and may not enter the 1-4 range WireGuard reserves. |
| `HeaderProtectionKey` | New in 3.0. ChaCha20 over the packet header, so the message type is unreadable rather than merely renamed. Needs `S1`-`S4` of at least 12: the nonce rides in that padding. Generate one with the button next to the field. |
| `RandomTrailers` | New in 3.0. Random trailing bytes on every packet. |

**May differ per client** - the fleet profile holds the default, and any client can override it:

| Setting | Notes |
| --- | --- |
| `Jc`, `Jmin`, `Jmax` | Junk packets before the handshake. |
| `I1`-`I5` | Obfuscation packets sent before the handshake, as a tag chain: `<b hex>`, `<t>`, `<r n>`, `<rc n>`, `<rd n>`, `<d>`, `<ds>`, `<dz>`. |
| `ContentPaddingAddition` | New in 3.0. Extra random payload bytes on top of the 16-byte multiple WireGuard already pads to. |
| `RekeyAfterTime` | Seconds before a live session re-handshakes. WireGuard uses a fixed 120. |
| `RekeyTimeout` | Seconds between handshake retries. Fixed 5 in WireGuard. |
| `RejectAfterTime` | Seconds before a key is abandoned. Fixed 180. Must stay above `RekeyAfterTime`. |
| `KeepaliveTimeout` | Seconds of silence before a keepalive. Fixed 10. |
| `MaxHandshakeAttempts` | Attempts before the peer gives up. Fixed 18. |
| `DisableCookies` | New in 3.0. Stops answering with a cookie reply under load, which is a recognizable message of its own. |
| `PersistentKeepalive` | Goes in the client `[Peer]`. Defaults to 25; a range here stops the whole fleet emitting one synchronized heartbeat. |

Differing per client is the point rather than a nicety: two clients that rekey on the same schedule
and pad to the same length are a correlatable pair.

Changing `HeaderProtectionKey`, `RandomTrailers`, `S1`-`S4` or `H1`-`H4` changes the wire format, so
**every client config has to be handed out again** - existing ones stop handshaking. The per-client
half can be changed freely; only that client's config needs reissuing.

### Upgrading a fleet to 3.x

Upgrade the panel first, then walk the nodes. Until a node is upgraded it keeps reporting the older
bundle schema, and the panel serves it a profile with the 3.x settings stripped and `H1`-`H4`
collapsed to their low bound, so its tunnel stays up on the settings it was already running. Such a
node is flagged on the **Nodes** page as `agent predates AmneziaWG 3.x`; do not turn on the 3.x
wire-format settings until that flag is gone from every node, or those nodes will be serving a wire
format your clients no longer speak.

On a node using the kernel data path, `scripts/install-awg-v3.sh` upgrades `awg`, `awg-quick` and
the DKMS module together, and verifies a real 3.x configuration on a throwaway interface before
leaving the module in place - rolling back to the userspace path if it cannot. Nodes running the
bundled `amneziawg-go` need nothing beyond the new image.

## Using the panel

- **Clients** — create, rename, enable, disable and delete clients; download a config, show a QR
  code, or create a 24-hour share link for someone without an account. Live traffic and handshake
  data is aggregated across every node, per client, and the counter can be zeroed from the panel:
  the kernel will not reset a peer counter without tearing the peer down, so the panel remembers
  where the counter stood and reports the difference.
  A share link shows only the QR code, the config and a link to the AmneziaWG build for whatever
  device opened it - never the name the client is filed under here.
- **Nodes** — status of each agent, its public address, whether it has picked up the current
  revision, which one clients are currently sent to, enrollment commands, and revocation. Revoking
  a node cuts off its configuration on its very next request.
- **Fleet** — the shared identity, obfuscation settings, and the legacy import.
- **Events** — an audit trail of who changed what and which nodes fetched configuration.

## Security model

- The admin API requires an authenticated session. Only `/api/health`, `/api/auth/*` and
  `/api/shares/*` are anonymous.
- Agents authenticate by **signing each request** with a key generated on the node itself. The
  private key never crosses the wire, and unlike mTLS this survives a TLS-terminating proxy or CDN
  in front of the panel.
- Configuration bundles are signed by the control plane and verified against a key the agent
  pinned at enrollment. Signatures alone are not enough — a correctly signed bundle stays valid
  forever — so bundles also carry a monotonic revision, a node binding and an expiry, and an agent
  refuses anything older than what it has already applied.
- Node revocation is a flag checked on every request, so it takes effect immediately. There is no
  revocation list to distribute.
- Enrollment and share tokens are stored only as hashes.

Assume that a hosting provider can read a node's disk. Treat any seized node as a compromise of
the whole fleet, since every node carries the shared identity.

## Development

```bash
dotnet build Awg-easy.sln
dotnet test Awg-easy.sln

cd frontend
pnpm install
pnpm run dev        # against a locally running control plane
pnpm run lint
pnpm run typecheck
```

| Project | Role |
| --- | --- |
| `src/AwgEasy.Contracts` | Wire contract shared by both sides. Changing it changes the fleet. |
| `src/AwgEasy.Node` | Agent for each VPN server. Native AOT, privileged, no UI. |
| `src/AwgEasy.Control` | Panel: database, admin API, agent API, serves the frontend. |
| `frontend` | Nuxt 4 + Nuxt UI, generated to static files. |
| `tests/AwgEasy.Tests` | Unit tests plus integration tests hosting the real control plane. |

The agent can be inspected without a control plane, which is useful when debugging a node:

```bash
awg-node --render-bundle bundle.json --control-key <base64url> --egress ens3
```

It verifies the signature, runs the acceptance checks and prints the interface config it would
apply, without touching anything.

See [AGENTS.md](AGENTS.md) for the architectural invariants worth knowing before changing code.

## Roadmap

Phase 1 — multi-server with manual switchover — is done. What comes next:

- **Health and blocking detection.** A blocked node looks perfectly healthy from the inside: the
  agent reports in, the interface is up, and clients simply cannot reach it. Detecting that needs
  probes from the networks users actually connect from, comparing agent heartbeats against real
  AmneziaWG handshakes from several vantage points.
- **Automatic failover** driven by that signal, with quorum and hysteresis, and notifying the
  operator. The switch itself already exists and is what a probe would call; what is missing is the
  judgement about when to call it. Reassigning a floating IP would be a second provider behind the
  same interface.
- **Fleet identity rotation**, so a compromised or seized node is recoverable without rebuilding
  everything by hand.

## Known gaps

- The pair has been exercised in containers on a single host, not yet across real servers.
- The frontend has no automated tests.
- The predecessor single-server app is preserved at tag `v0.9-standalone` and has been removed
  from the tree.

---

# AWG Easy на русском

Self-hosted панель управления флотом серверов AmneziaWG.

Одна панель — источник истины для клиентов, ключей и настроек обфускации. Агенты на каждом
VPN-сервере забирают эту конфигурацию и приводят себя к ней. Смысл флота — **бесшовное
переключение**: когда сервер блокируют или он падает, трафик переезжает на другой без
перевыпуска хотя бы одного клиентского конфига.

> Статус: архитектура флота готова, переключение **ручное** — одна кнопка в панели переводит
> DNS-запись на другую ноду. Автофейловер, внешние пробы и уведомления спроектированы, но ещё не
> построены. См. [Дорожную карту](#дорожная-карта).

## Как это работает

Все ноды используют **одну и ту же серверную идентичность AmneziaWG** — общую ключевую пару,
подсеть и профиль обфускации. Клиентский конфиг привязан к публичному ключу сервера, поэтому
переключение DNS на другую ноду не меняет того, с чем разговаривает клиент. Он просто продолжает
работать.

Ноды делают только **исходящие** запросы, поэтому не открывают ни одного управляющего порта —
наружу смотрит лишь UDP-туннель. И они **fail-static**: если панель недоступна, агент продолжает
обслуживать трафик по закэшированной конфигурации сколько угодно долго. Потеря управления никогда
не означает потерю VPN.

Нода получает только то, что нужно для обслуживания пиров, — публичные ключи, preshared-ключи и
адреса. Приватные ключи клиентов и их имена панель не отдаёт никогда.

## Что нужно для старта

- **Хост для панели.** Любой небольшой VPS. Держите его отдельно от VPN-нод: на нём лежит
  идентичность всего флота, а ей не место на серверах, которые с наибольшей вероятностью
  заблокируют или изымут.
- **Хотя бы одна VPN-нода.** Root, Docker, `/dev/net/tun` и открытый UDP-порт. Агент работает в
  host-режиме сети, поэтому займёт на сервере ещё `127.0.0.1:8081` под health-эндпоинт
  (меняется через `AWG_HEALTH_URL`).
- **DNS-запись, которой вы управляете.** `AWG_ENDPOINT_HOST` попадает в клиентские конфиги, и
  переключение — это смена этой записи. Поставьте TTL 30–60 секунд заранее. Дайте панели токен
  Cloudflare API — и она будет менять запись сама; без токена она скажет, что вписать вручную.
  Если провайдер даёт floating IP, он лучше: переезд мгновенный и совершенно незаметный для
  клиентов.
- **TLS перед панелью.** По обычному HTTP она отдаст агенту приватный ключ флота в открытом виде.
  Поставьте перед ней Caddy или nginx с Let's Encrypt.

## Запуск панели

```bash
cp .env.control.example .env
```

Отредактируйте `.env` — как минимум `AWG_ENDPOINT_HOST`, публичное имя для подключения клиентов, и
учётные данные администратора. Без endpoint host панель откажется стартовать: это значение
попадает в каждый клиентский конфиг, и неверное даёт конфиги, которые никуда не подключаются.

```bash
docker compose -f compose.control.yaml up -d --build
```

Сборка сама по себе конфигурации не требует, так что образы можно проверить заранее:

```bash
docker compose -f compose.control.yaml build
docker compose -f compose.node.yaml build
```

### Не собирайте образы на маленькой ноде

Сборка тянет .NET SDK, образ Node и Go-тулчейн — вместе это несколько гигабайт, которых на
сервере с диском 10 ГБ просто нет. Именно поэтому compose-файлы ссылаются на опубликованные
образы: соберите один раз там, где есть место, запушьте, а на серверах только `pull`.

```bash
# На машине сборки или в CI. AGENT_VERSION — это версия, которую нода сообщает
# панели; без неё агент представляется как 0.0.0-dev.
docker build -f docker/control.Dockerfile -t dihasi/awg-control:latest .
docker build -f docker/node.Dockerfile   -t dihasi/awg-node:latest --build-arg AGENT_VERSION=1.3.1 .
docker push dihasi/awg-control:latest && docker push dihasi/awg-node:latest
```

```bash
# На серверах
docker compose -f compose.control.yaml pull && docker compose -f compose.control.yaml up -d
```

Нода тогда качает только рантайм-образ, а не тулчейны, которыми он собран.

При первом старте создаётся база, генерируются идентичность флота и ключ подписи, заводится
аккаунт администратора. Панель доступна на `http://<хост>:8080`.

Без `AWG_ADMIN_USER` / `AWG_ADMIN_PASSWORD` панель запустится, но откажет во всех админских
запросах и напишет об этом в лог. Так сделано намеренно: падать из-за опечатки в переменной
окружения хуже, чем стартовать запертым.

## Подключение ноды

В панели: **Nodes → Add node**. Вы получите готовую команду:

```bash
curl -fsSL https://panel.example.com/install.sh | sh -s -- --url https://panel.example.com --token <token>
```

Токен показывается **один раз**, восстановить его нельзя, живёт 30 минут. Установщик при
необходимости поставит Docker, включит форвардинг и запустит агента, который сам зарегистрируется,
запомнит ключ подписи панели и заберёт конфигурацию. Повторный запуск безопасен — это обновление
агента, идентичность ноды сохраняется, туннель не рвётся.

Если предпочитаете compose — используйте `.env.node.example` вместе с `compose.node.yaml`.

### Правки под конкретный сервер

Не редактируйте на сервере compose-файлы из репозитория — каждый `git pull` будет конфликтовать.
Локальные отклонения кладите в override-файл, он в `.gitignore`, и передавайте оба:

```bash
docker compose -f compose.control.yaml -f compose.control.override.yaml up -d
```

```yaml
# compose.control.override.yaml
services:
  awg-control:
    ports:
      - "127.0.0.1:8080:8080"   # доступ только через reverse proxy
```

Обычные значения — хосты, порты, учётные данные — задавайте в `.env`.

## Переключение ноды для клиентов

В каждом клиентском конфиге указано одно имя хоста и прибит публичный ключ флота. Поэтому
фейловер — это всего лишь заставить это имя разрешаться в другую ноду: тот же ключ, та же подсеть,
тот же профиль обфускации, другой сервер. Ничего не перевыпускается, ни одна нода ничего не
переприменяет, и ревизия флота осознанно остаётся на месте.

В панели: **Nodes → Make active** на нужной ноде. У каждой ноды показан внешний адрес, который её
агент определил сам — именно на него и переводится запись, — а карточка сверху показывает саму
запись, её TTL и согласен ли с ней уже резолвер самой панели.

Если заданы `AWG_CLOUDFLARE_API_TOKEN` и `AWG_CLOUDFLARE_ZONE_ID`, панель правит запись сама —
A или AAAA, всегда без проксирования: оранжевая тучка пропускает только HTTP и проглотила бы
туннель. Токену нужно право `Zone:DNS:Edit` на эту зону и ничего больше. Если API отказал,
переключение отменяется вместе с ним: панель никогда не утверждает, что нода активна, пока запись
смотрит в другую сторону.

Без токена кнопка тоже работает — она фиксирует активную ноду и показывает, что вписать, — а
проверка резолвом подтверждает, что вы это сделали.

Клиенты переезжают по мере того, как у резолверов истекает выданный им ответ, так что TTL записи и
есть реальное ограничение скорости. Доступность ноды для этого не нужна: уход с мёртвой ноды — это
ровно тот случай, для которого всё и сделано.

Если у ноды вообще нет адреса, её агент не смог достучаться ни до одного эхо-сервиса. Задайте на
этой ноде `AWG_PUBLIC_IP` напрямую.

## Восстановление ноды

Отзыв или удаление ноды в панели обрывает выдачу конфигурации, но **не останавливает туннель**:
агент продолжает обслуживать трафик по закэшированному бандлу. Это работает fail-static, а не
поломка.

Чтобы вернуть такой сервер в строй, выпустите новый токен и заново запустите установщик. Если
старая нода ещё числится в панели — сначала удалите её: агент хранит одну ключевую пару на всю
жизнь, и панель не даст зарегистрировать тот же сервер дважды.

Если нужно выбросить саму идентичность ноды — потерянный или подозрительный сервер — сотрите её
состояние, и она зарегистрируется с нуля:

```bash
docker rm -f awg-node && rm -rf /etc/awg-node/*
```

## Переезд с одиночного сервера

Текущие пользователи не отваливаются, потому что импорт сохраняет исходную ключевую пару сервера.

1. Выгрузите бэкап со старого сервера: `GET /api/backups/export`.
2. В панели: **Fleet → Import**, включите *Replace existing clients*, загрузите файл.
3. Убедитесь, что **Server public key** на странице Fleet совпадает со старым.
4. Замените на этом сервере старый контейнер агентом (командой выше).
5. Агент применит конфигурацию через `awg syncconf` — интерфейс не опускается, существующие
   туннели переживают переход. Исключение — подсеть или MTU, отличающиеся от того, что интерфейс
   уже несёт: применить их на живом интерфейсе нельзя, поэтому агент один раз перезапустит его и
   напишет об этом в лог.

Для чистой панели можно вместо этого указать `AWG_IMPORT_LEGACY_STATE` на смонтированный
`state.json`.

## Настройка

### Панель

| Переменная | Назначение |
| --- | --- |
| `AWG_ENDPOINT_HOST` | Что клиенты видят как endpoint. **Это и есть точка переключения.** |
| `AWG_PORT` | UDP-порт для подключения клиентов. |
| `AWG_SUBNET` | Подсеть туннеля; адреса выделяются из неё на весь флот. |
| `AWG_CLIENT_ALLOWED_IPS` | Значение `AllowedIPs` в клиентских конфигах. |
| `AWG_CLIENT_DNS` | DNS-серверы в клиентских конфигах. |
| `AWG_ADMIN_USER`, `AWG_ADMIN_PASSWORD` | Создание первого администратора на пустой базе. |
| `AWG_CONTROL_DB` | Файл SQLite. По умолчанию `/etc/awg-control/control.db`. |
| `AWG_BUNDLE_LIFETIME_MINUTES` | Срок жизни подписанного бандла. По умолчанию 15. |
| `AWG_IMPORT_LEGACY_STATE` | Разовое усыновление старого `state.json`. |
| `AWG_CLOUDFLARE_API_TOKEN` | Позволяет панели менять запись сама. Нужно право `Zone:DNS:Edit`. |
| `AWG_CLOUDFLARE_ZONE_ID` | Зона, в которой живёт запись. Без обоих значений переключение остаётся ручным. |
| `AWG_DNS_RECORD_NAME` | Какую запись переводить. По умолчанию `AWG_ENDPOINT_HOST`. |
| `AWG_DNS_TTL` | TTL записи, он же ограничение скорости фейловера. По умолчанию 60. |

### Нода

| Переменная | Назначение |
| --- | --- |
| `AWG_CONTROL_URL` | Адрес панели. |
| `AWG_ENROLLMENT_TOKEN` | Нужен только для первого запуска. |
| `AWG_EGRESS_INTERFACE` | Оставьте пустым — интерфейс определится по default route. |
| `AWG_HEALTH_URL` | Health-эндпоинт агента. Биндится на хосте. По умолчанию `http://127.0.0.1:8081`. |
| `AWG_POLL_INTERVAL_SECONDS` | Частота опроса новой ревизии. По умолчанию 20. |
| `AWG_NODE_STATE_PATH` | Идентичность агента и кэш бандла. По умолчанию `/etc/awg-node`. |
| `AWG_INTERFACE` | Имя интерфейса. По умолчанию `awg0`. |
| `AWG_PUBLIC_IP` | Задать внешний адрес ноды вручную вместо автоопределения. |
| `AWG_PUBLIC_IP_URLS` | Эхо-сервисы для автоопределения. Пустое значение выключает его. |
| `AWG_PUBLIC_IP_REFRESH_MINUTES` | Как часто перепроверять известный адрес. По умолчанию 10. |

Обфускация настраивается **не** переменными окружения. Она общая для флота и задаётся в панели в
разделе **Fleet**, поэтому изменение применяется сразу ко всем нодам.

## Обфускация (AmneziaWG 3.x)

Панель рассчитана на линейку AmneziaWG 3.1. Смысл 3.x в том, что маскировать отдельные признаки
перестало хватать: туннель со случайными размерами пакетов, но фиксированным интервалом rekey и
фиксированным ритмом keepalive всё равно узнаётся. Поэтому большинство новых параметров — это не
значения, а *диапазоны* в виде `lo-hi`, и пир заново выбирает число внутри диапазона при каждом
использовании. Одно число тоже работает, а `0`, `0-0` или `off` означает поведение обычного
WireGuard.

Параметры делятся на две группы, и от группы зависит, где их настраивать.

**Должны совпадать на обоих концах** — общие для флота, пишутся и в конфиг интерфейса каждой ноды,
и в каждый клиентский конфиг:

| Параметр | Замечания |
| --- | --- |
| `S1`-`S4` | Случайный паддинг для init, response, cookie и transport. |
| `H1`-`H4` | Идентификаторы типов сообщений. Теперь диапазоны; четыре не должны пересекаться и не должны попадать в зарезервированный WireGuard диапазон 1-4. |
| `HeaderProtectionKey` | Новое в 3.0. ChaCha20 поверх заголовка пакета: тип сообщения не просто переименован, а нечитаем. Требует `S1`-`S4` не меньше 12 — nonce едет внутри этого паддинга. Сгенерировать можно кнопкой рядом с полем. |
| `RandomTrailers` | Новое в 3.0. Случайные байты в конце каждого пакета. |

**Могут различаться у каждого клиента** — в профиле флота лежит значение по умолчанию, любой клиент
может его переопределить:

| Параметр | Замечания |
| --- | --- |
| `Jc`, `Jmin`, `Jmax` | Мусорные пакеты перед хендшейком. |
| `I1`-`I5` | Обфускационные пакеты перед хендшейком, цепочкой тегов: `<b hex>`, `<t>`, `<r n>`, `<rc n>`, `<rd n>`, `<d>`, `<ds>`, `<dz>`. |
| `ContentPaddingAddition` | Новое в 3.0. Добавка случайных байт к полезной нагрузке сверх кратности 16. |
| `RekeyAfterTime` | Секунды до повторного хендшейка живой сессии. В WireGuard фиксированные 120. |
| `RekeyTimeout` | Секунды между повторами хендшейка. В WireGuard 5. |
| `RejectAfterTime` | Секунды до отказа от ключа. В WireGuard 180. Должно оставаться больше `RekeyAfterTime`. |
| `KeepaliveTimeout` | Секунды тишины до keepalive. В WireGuard 10. |
| `MaxHandshakeAttempts` | Число попыток хендшейка. В WireGuard 18. |
| `DisableCookies` | Новое в 3.0. Не отвечать cookie reply под нагрузкой — сам этот ответ является узнаваемым сообщением. |
| `PersistentKeepalive` | Пишется в `[Peer]` клиента. По умолчанию 25; диапазон здесь убирает единый синхронный «пульс» всего флота. |

Различие между клиентами — это цель, а не мелочь: два клиента с одинаковым расписанием rekey и
одинаковым паддингом коррелируются между собой.

Изменение `HeaderProtectionKey`, `RandomTrailers`, `S1`-`S4` или `H1`-`H4` меняет формат на проводе,
поэтому **все клиентские конфиги придётся выдать заново** — старые перестанут хендшейкиться.
Вторую группу можно менять свободно: переоформить нужно только конфиг этого клиента.

### Перевод флота на 3.x

Сначала обновляется панель, потом по очереди ноды. Пока нода не обновлена, она сообщает старую
версию схемы бандла, и панель отдаёт ей профиль без параметров 3.x и с `H1`-`H4`, сведёнными к
нижней границе, — её туннель продолжает работать на тех настройках, что уже были. Такая нода
помечается на странице **Nodes** как `agent predates AmneziaWG 3.x`; не включайте параметры формата
на проводе, пока эта метка не исчезнет со всех нод, иначе эти ноды будут отдавать формат, которого
клиенты уже не понимают.

Для ноды на kernel-датапасе есть `scripts/install-awg-v3.sh`: он обновляет `awg`, `awg-quick` и
DKMS-модуль вместе, проверяет настоящий конфиг 3.x на одноразовом интерфейсе и откатывается на
userspace, если проверка не прошла. Нодам на встроенном `amneziawg-go` достаточно нового образа.

## Работа с панелью

- **Clients** — создание, переименование, включение, отключение и удаление клиентов; скачивание
  конфига, QR-код, share-ссылка на 24 часа для того, у кого нет аккаунта. Статистика трафика и
  handshake агрегируется по всем нодам, по каждому клиенту, и счётчик можно обнулить из панели:
  ядро не сбрасывает счётчик пира без пересоздания самого пира, поэтому панель запоминает его
  текущее значение и дальше показывает разницу.
  Share-страница показывает только QR-код, конфиг и ссылку на сборку AmneziaWG под то устройство,
  с которого её открыли, — но не имя, под которым клиент записан в панели.
- **Nodes** — состояние агентов, их внешние адреса, забрали ли они текущую ревизию, куда сейчас
  ходят клиенты, команды подключения и отзыв доступа. Отзыв обрывает выдачу конфигурации со
  следующего же запроса ноды.
- **Fleet** — общая идентичность, настройки обфускации, импорт со старого сервера.
- **Events** — журнал: кто что менял и какие ноды забирали конфигурацию.

## Модель безопасности

- Админский API требует аутентифицированной сессии. Анонимны только `/api/health`, `/api/auth/*`
  и `/api/shares/*`.
- Агенты аутентифицируются **подписью каждого запроса** ключом, который сгенерирован на самой
  ноде. Приватный ключ никогда не уходит по сети, и, в отличие от mTLS, это переживает
  терминирующий TLS прокси или CDN перед панелью.
- Бандлы конфигурации подписываются панелью и проверяются ключом, который агент запомнил при
  регистрации. Одной подписи мало — корректно подписанный бандл остаётся валидным вечно, — поэтому
  бандл несёт монотонную ревизию, привязку к ноде и срок годности, а агент отвергает всё, что
  старше уже применённого.
- Отзыв ноды — флаг, проверяемый на каждом запросе, поэтому он действует немедленно. Никаких
  списков отзыва распространять не нужно.
- Токены регистрации и share-ссылок хранятся только в виде хешей.

Исходите из того, что хостер может прочитать диск ноды. Считайте изъятую ноду компрометацией
всего флота, поскольку общая идентичность есть на каждой.

## Разработка

```bash
dotnet build Awg-easy.sln
dotnet test Awg-easy.sln

cd frontend
pnpm install
pnpm run dev        # против локально запущенной панели
pnpm run lint
pnpm run typecheck
```

| Проект | Роль |
| --- | --- |
| `src/AwgEasy.Contracts` | Общий контракт обеих сторон. Его изменение меняет весь флот. |
| `src/AwgEasy.Node` | Агент для VPN-сервера. Native AOT, привилегированный, без UI. |
| `src/AwgEasy.Control` | Панель: база, админский API, агентский API, раздача фронтенда. |
| `frontend` | Nuxt 4 + Nuxt UI, собирается в статику. |
| `tests/AwgEasy.Tests` | Модульные тесты плюс интеграционные с настоящей панелью. |

Агента можно проверить без панели — удобно при отладке ноды:

```bash
awg-node --render-bundle bundle.json --control-key <base64url> --egress ens3
```

Он проверит подпись, прогонит проверки приёмки и напечатает конфиг интерфейса, который применил
бы, ничего при этом не трогая.

Архитектурные инварианты, которые стоит знать перед правкой кода, собраны в [AGENTS.md](AGENTS.md).

## Дорожная карта

Фаза 1 — мультисерверность с ручным переключением — готова. Дальше:

- **Детект блокировки.** Заблокированная нода изнутри выглядит совершенно здоровой: агент
  рапортует, интерфейс поднят, а клиенты просто не могут до неё достучаться. Чтобы это увидеть,
  нужны пробы из сетей, откуда реально подключаются пользователи, и сопоставление heartbeat агента
  с настоящими AmneziaWG-хендшейками с нескольких точек.
- **Автоматический фейловер** по этому сигналу — с кворумом, гистерезисом и уведомлением
  администратора. Само переключение уже есть, и проба будет вызывать именно его; не хватает
  решения о том, когда его вызывать. Перенос floating IP — это второй провайдер за тем же
  интерфейсом.
- **Ротация идентичности флота**, чтобы скомпрометированная или изъятая нода не означала ручную
  пересборку всего.
## Известные пробелы

- Связка проверена в контейнерах на одной машине, но ещё не на разнесённых серверах.
- У фронтенда нет автоматических тестов.
- Предшественник — одиночное приложение — сохранён под тегом `v0.9-standalone` и удалён из
  дерева.

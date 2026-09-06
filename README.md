# AWG Easy

A self-hosted control plane for a fleet of AmneziaWG VPN servers.

One panel is the source of truth for clients, keys and obfuscation settings. Agents on each VPN
server pull that configuration and converge onto it. The point of the fleet design is **seamless
failover**: when a server is blocked or dies, you move traffic to another one without reissuing a
single client config.

> Status: the fleet architecture is in place with **manual switchover**. Automatic failover,
> external probes and notifications are designed but not yet built. See [Roadmap](#roadmap).

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
  repointing it. Set the TTL to 30–60 seconds ahead of time. If your provider offers a floating
  IP, prefer it — reassignment is instant and completely invisible to clients.
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
# On a build machine or in CI
docker build -f docker/control.Dockerfile -t dihasi/awg-control:latest .
docker build -f docker/node.Dockerfile   -t dihasi/awg-node:latest .
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
   down and existing tunnels survive.

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

Obfuscation is **not** configured through environment variables. It is fleet-wide and lives in the
panel under **Fleet**, so a change applies to every node at once.

## Using the panel

- **Clients** — create, rename, enable, disable and delete clients; download a config, show a QR
  code, or create a 24-hour share link for someone without an account. Live traffic and handshake
  data is aggregated across every node.
- **Nodes** — status of each agent, whether it has picked up the current revision, enrollment
  commands, and revocation. Revoking a node cuts off its configuration on its very next request.
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
- **Automatic failover** driven by that signal, with quorum and hysteresis, updating DNS or
  reassigning a floating IP, and notifying the operator.
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

> Статус: архитектура флота готова, переключение **ручное**. Автофейловер, внешние пробы и
> уведомления спроектированы, но ещё не построены. См. [Дорожную карту](#дорожная-карта).

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
  переключение — это смена этой записи. Поставьте TTL 30–60 секунд заранее. Если провайдер даёт
  floating IP, он лучше: переезд мгновенный и совершенно незаметный для клиентов.
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
# На машине сборки или в CI
docker build -f docker/control.Dockerfile -t dihasi/awg-control:latest .
docker build -f docker/node.Dockerfile   -t dihasi/awg-node:latest .
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
   туннели переживают переход.

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

Обфускация настраивается **не** переменными окружения. Она общая для флота и задаётся в панели в
разделе **Fleet**, поэтому изменение применяется сразу ко всем нодам.

## Работа с панелью

- **Clients** — создание, переименование, включение, отключение и удаление клиентов; скачивание
  конфига, QR-код, share-ссылка на 24 часа для того, у кого нет аккаунта. Статистика трафика и
  handshake агрегируется по всем нодам.
- **Nodes** — состояние агентов, забрали ли они текущую ревизию, команды подключения и отзыв
  доступа. Отзыв обрывает выдачу конфигурации со следующего же запроса ноды.
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
- **Автоматический фейловер** по этому сигналу — с кворумом и гистерезисом, с обновлением DNS или
  переносом floating IP и уведомлением администратора.
- **Ротация идентичности флота**, чтобы скомпрометированная или изъятая нода не означала ручную
  пересборку всего.

## Известные пробелы

- Связка проверена в контейнерах на одной машине, но ещё не на разнесённых серверах.
- У фронтенда нет автоматических тестов.
- Предшественник — одиночное приложение — сохранён под тегом `v0.9-standalone` и удалён из
  дерева.

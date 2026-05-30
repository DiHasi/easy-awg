# AWG Easy

AWG Easy is a self-hosted web panel for managing AmneziaWG clients. The project follows the idea of `wg-easy`, but is built around AmneziaWG and includes support for traffic obfuscation parameters.

The application is designed to run as a single Docker container: the backend manages the VPN configuration and API, while the frontend is built as static files and served by the same ASP.NET application.

## What It Does

- Creates AmneziaWG client configurations.
- Deletes clients that are no longer needed.
- Temporarily disables and enables existing clients.
- Generates downloadable client config files.
- Stores state in a JSON file, without a database.
- Applies server-side AmneziaWG obfuscation settings.
- Allows optional client-side obfuscation overrides when creating a client.
- Shows client status, last handshake, current traffic speed, and total traffic usage.
- Provides a web UI for daily administration.
- Provides OpenAPI documentation with Scalar.

## Technology Stack

The backend is built with ASP.NET Core on .NET 10 and published with Native AOT. This keeps the runtime small and predictable for container deployment.

The frontend is built with Nuxt 4 and Nuxt UI. It is generated as static files during the Docker build and then served directly by the backend from the application root.

AmneziaWG integration is handled inside the container. The Docker image builds and includes:

- `awg`
- `awg-quick`
- `amneziawg-go`

If the host kernel supports the AmneziaWG kernel module, the kernel implementation can be used. Otherwise, the container falls back to the userspace implementation through `amneziawg-go`.

## Main Features

### Client Management

Clients can be created, disabled, enabled, deleted, and downloaded as ready-to-use configuration files. Each client receives its own keys, address, preshared key, and config.

### Obfuscation Settings

Server obfuscation parameters are configured centrally and then applied to generated AmneziaWG server configuration. Client parameters can be optionally overridden during client creation.

This keeps the common server-side values consistent while still allowing per-client tuning where needed.

### Live Client Statistics

The frontend receives client statistics through a lightweight event stream from the backend. It shows:

- whether the client is online;
- latest handshake time;
- current download and upload speed;
- total downloaded traffic;
- total uploaded traffic.

The current speed is calculated from traffic changes between updates. Total traffic is shown separately, so offline clients do not look like they are actively transferring data.

### Static Frontend

The Nuxt frontend is not deployed as a separate Node.js service. During the Docker build it is generated into static files and copied into the ASP.NET application. The backend serves it from `/`, while the API remains available under `/api`.

## Configuration

Configuration is provided through environment variables. A ready-to-edit example is available in `.env.example`.

```env
WEB_PORT=8080
AWG_PORT=51820
AWG_SUBNET=10.8.0.0/24
AWG_CLIENT_ALLOWED_IPS=0.0.0.0/0, ::/0
AWG_ENDPOINT_HOST=vpn.example.com
AWG_CLIENT_DNS=1.1.1.1, 8.8.8.8
```

### Environment Variables

| Variable | Description |
| --- | --- |
| `WEB_PORT` | Port for the web interface and API. |
| `AWG_PORT` | UDP port used by AmneziaWG clients. |
| `AWG_SUBNET` | Internal VPN subnet used for client addresses. |
| `AWG_CLIENT_ALLOWED_IPS` | `AllowedIPs` value placed into generated client configs. |
| `AWG_ENDPOINT_HOST` | Public hostname or IP that clients use as the VPN endpoint. |
| `AWG_CLIENT_DNS` | DNS servers written into client configs. |

## Running With Docker Compose

Create a local `.env` file:

```bash
cp .env.example .env
```

Edit `.env` and set at least `AWG_ENDPOINT_HOST` to the public hostname or IP address of your server.

Then build and start the container:

```bash
docker compose up -d --build
```

The web interface will be available at:

```text
http://localhost:8080
```

If `WEB_PORT` is changed, use that port instead.

## Docker Requirements

The container needs network administration permissions and access to `/dev/net/tun`. The included `compose.yaml` already configures this:

- `NET_ADMIN`
- `SYS_MODULE`
- `/dev/net/tun`
- IPv4 forwarding sysctls

On a real server, make sure the chosen UDP port is open in the firewall.

## API And Documentation

The backend exposes the API under `/api`.

Useful endpoints include:

| Endpoint | Purpose |
| --- | --- |
| `GET /api/health` | Health check. |
| `GET /api/server` | Current server configuration. |
| `GET /api/server/runtime` | Runtime status of the AmneziaWG interface. |
| `PUT /api/server/obfuscation` | Update server obfuscation settings. |
| `GET /api/clients` | List clients. |
| `POST /api/clients` | Create a client. |
| `POST /api/clients/{id}/disable` | Disable a client. |
| `POST /api/clients/{id}/enable` | Enable a client. |
| `DELETE /api/clients/{id}` | Delete a client. |
| `GET /api/clients/{id}/config` | Download a client config. |
| `GET /api/clients/events` | Live client statistics event stream. |

OpenAPI is available at:

```text
/openapi/v1.json
```

Scalar API documentation is available at:

```text
/scalar
```

## State And Persistence

The application stores its state in a JSON file. By default in Docker this is mounted through the `awg-easy-state` volume.

The generated AmneziaWG interface configuration is stored in a separate volume mounted to:

```text
/etc/amnezia/amneziawg
```

This means container rebuilds do not automatically remove clients or server keys, as long as the Docker volumes are kept.

## Notes

This project is currently focused on practical single-server deployment. It intentionally avoids a database and keeps configuration in files. That makes backup, inspection, and recovery straightforward.

The current synchronization model writes the AmneziaWG configuration and applies it to the running interface. The implementation is simple and reliable for this stage of the project. More advanced synchronization can be improved later if needed.

---

# AWG Easy на русском

AWG Easy - это self-hosted веб-панель для управления клиентами AmneziaWG. По идее проект похож на `wg-easy`, но ориентирован именно на AmneziaWG и поддерживает параметры обфускации трафика.

Приложение рассчитано на запуск в одном Docker-контейнере. Backend управляет VPN-конфигурацией и API, а frontend собирается в статические файлы и отдается тем же ASP.NET-приложением.

## Что Умеет Проект

- Создавать конфигурации клиентов AmneziaWG.
- Удалять клиентов.
- Временно отключать и включать клиентов обратно.
- Скачивать готовый клиентский конфиг.
- Хранить состояние в JSON-файле, без базы данных.
- Применять серверные параметры обфускации AmneziaWG.
- Задавать клиентские параметры обфускации при создании клиента.
- Показывать статус клиента, последний handshake, текущую скорость и общий расход трафика.
- Давать удобный web-интерфейс для администрирования.
- Показывать OpenAPI-документацию через Scalar.

## Используемые Технологии

Backend написан на ASP.NET Core под .NET 10 и публикуется через Native AOT. Это делает приложение более компактным и удобным для контейнерного запуска.

Frontend сделан на Nuxt 4 и Nuxt UI. Во время Docker-сборки он генерируется в статические файлы, после чего backend отдает его с корня сайта.

Внутри Docker-образа собираются и используются инструменты AmneziaWG:

- `awg`
- `awg-quick`
- `amneziawg-go`

Если ядро хоста поддерживает модуль AmneziaWG, может использоваться kernel-реализация. Если модуль недоступен, контейнер может работать через userspace-реализацию `amneziawg-go`.

## Основной Функционал

### Управление Клиентами

Клиентов можно создавать, отключать, включать, удалять и скачивать для них готовые конфигурационные файлы. Для каждого клиента генерируются ключи, адрес, preshared key и полноценный конфиг.

### Обфускация

Серверные параметры обфускации задаются централизованно и применяются к конфигурации AmneziaWG-интерфейса. При создании клиента можно дополнительно указать клиентские параметры, если для конкретного устройства нужны отдельные значения.

Такой подход сохраняет единые серверные настройки и при этом оставляет возможность гибкой настройки клиентов.

### Live-Статистика

Frontend получает статистику через легкий event stream от backend. В интерфейсе отображается:

- online/offline статус клиента;
- время последнего handshake;
- текущая скорость скачивания и отправки;
- общий скачанный трафик;
- общий отправленный трафик.

Текущая скорость считается по изменению счетчиков между обновлениями. Общий трафик показывается отдельно, поэтому offline-клиенты не выглядят так, будто они продолжают активно передавать данные.

### Статический Frontend

Nuxt не запускается отдельным Node.js-сервисом в production. Он собирается в статику во время Docker build и копируется внутрь ASP.NET-приложения. Web-интерфейс доступен с `/`, а API остается на `/api`.

## Настройка

Настройки задаются через переменные окружения. Пример находится в `.env.example`.

```env
WEB_PORT=8080
AWG_PORT=51820
AWG_SUBNET=10.8.0.0/24
AWG_CLIENT_ALLOWED_IPS=0.0.0.0/0, ::/0
AWG_ENDPOINT_HOST=vpn.example.com
AWG_CLIENT_DNS=1.1.1.1, 8.8.8.8
```

### Переменные Окружения

| Переменная | Описание |
| --- | --- |
| `WEB_PORT` | Порт web-интерфейса и API. |
| `AWG_PORT` | UDP-порт для подключения AmneziaWG-клиентов. |
| `AWG_SUBNET` | Внутренняя VPN-подсеть для адресов клиентов. |
| `AWG_CLIENT_ALLOWED_IPS` | Значение `AllowedIPs`, которое попадет в клиентские конфиги. |
| `AWG_ENDPOINT_HOST` | Публичный домен или IP-адрес сервера для подключения клиентов. |
| `AWG_CLIENT_DNS` | DNS-серверы, которые будут записаны в клиентские конфиги. |

## Запуск Через Docker Compose

Создайте локальный `.env`:

```bash
cp .env.example .env
```

Отредактируйте `.env` и обязательно укажите `AWG_ENDPOINT_HOST` - публичный домен или IP-адрес вашего сервера.

После этого соберите и запустите контейнер:

```bash
docker compose up -d --build
```

Web-интерфейс будет доступен по адресу:

```text
http://localhost:8080
```

Если вы изменили `WEB_PORT`, используйте выбранный порт.

## Требования Для Docker

Контейнеру нужны права на управление сетью и доступ к `/dev/net/tun`. В `compose.yaml` это уже настроено:

- `NET_ADMIN`
- `SYS_MODULE`
- `/dev/net/tun`
- sysctl-настройки для IPv4 forwarding

На реальном сервере также нужно открыть выбранный UDP-порт в firewall.

## API И Документация

API доступно по пути `/api`.

Основные endpoints:

| Endpoint | Назначение |
| --- | --- |
| `GET /api/health` | Проверка состояния приложения. |
| `GET /api/server` | Текущая конфигурация сервера. |
| `GET /api/server/runtime` | Runtime-статус AmneziaWG-интерфейса. |
| `PUT /api/server/obfuscation` | Обновление серверных параметров обфускации. |
| `GET /api/clients` | Список клиентов. |
| `POST /api/clients` | Создание клиента. |
| `POST /api/clients/{id}/disable` | Отключение клиента. |
| `POST /api/clients/{id}/enable` | Включение клиента. |
| `DELETE /api/clients/{id}` | Удаление клиента. |
| `GET /api/clients/{id}/config` | Скачивание клиентского конфига. |
| `GET /api/clients/events` | Event stream со статистикой клиентов. |

OpenAPI доступен здесь:

```text
/openapi/v1.json
```

Scalar-документация доступна здесь:

```text
/scalar
```

## Хранение Данных

Состояние приложения хранится в JSON-файле. При запуске через Docker оно сохраняется в volume `awg-easy-state`.

Сгенерированная конфигурация AmneziaWG-интерфейса хранится отдельно:

```text
/etc/amnezia/amneziawg
```

Пока Docker volumes сохраняются, пересборка контейнера не удаляет клиентов и серверные ключи.

## Заметки

Проект сейчас ориентирован на практичное развертывание на одном сервере. База данных намеренно не используется: состояние хранится в файлах, которые проще бэкапить, проверять и восстанавливать.

Текущая модель синхронизации записывает конфигурацию AmneziaWG и применяет ее к интерфейсу. Это простой и надежный вариант для текущего этапа. Позже его можно заменить на более аккуратную синхронизацию, если это понадобится.

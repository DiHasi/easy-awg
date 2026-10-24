# Documentation · Документация

Every page below exists in both languages, and the two are kept in step — the pair of links in
each row points at the same content. A language switch sits at the top of every page.

Каждая страница ниже есть на двух языках, и они держатся в соответствии друг с другом — пара
ссылок в строке ведёт на одно и то же. Переключатель языка — в начале каждой страницы.

| | English | Русский |
| --- | --- | --- |
| What this is and how it works | [Overview](en/overview.md) | [Обзор](ru/overview.md) |
| Getting the panel and the first node up | [Getting started](en/getting-started.md) | [Запуск](ru/getting-started.md) |
| Day-to-day work in the panel | [Using the panel](en/panel.md) | [Работа с панелью](ru/panel.md) |
| Moving traffic between nodes, and the probes that decide when to | [Failover and probes](en/failover.md) | [Фейловер и пробы](ru/failover.md) |
| Surviving the loss of the panel itself | [A second panel](en/panel-pair.md) | [Вторая панель](ru/panel-pair.md) |
| The AmneziaWG 3.x wire format and the tunnel MTU | [Obfuscation and MTU](en/obfuscation.md) | [Обфускация и MTU](ru/obfuscation.md) |
| What is trusted with what, and how to rotate the fleet identity | [Security model](en/security.md) | [Модель безопасности](ru/security.md) |
| Metrics, node recovery, migrating from a single server | [Operations](en/operations.md) | [Эксплуатация](ru/operations.md) |
| Every environment variable, with its default | [Configuration reference](en/configuration.md) | [Справочник настроек](ru/configuration.md) |
| Building, testing and running the whole pair | [Development](en/development.md) | [Разработка](ru/development.md) |
| What is next and what is not covered | [Roadmap and known gaps](en/roadmap.md) | [Дорожная карта и пробелы](ru/roadmap.md) |

The architectural invariants — the rules that break the fleet silently when broken — are in
[AGENTS.md](../AGENTS.md) rather than here, because they are written for whoever changes the code.

Инварианты архитектуры — правила, нарушение которых ломает флот молча, — лежат в
[AGENTS.md](../AGENTS.md), а не здесь: они написаны для того, кто меняет код.

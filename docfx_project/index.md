# Kouki — платформа для оркестрации команд

Kouki — это набор библиотек для создания автоматизированных систем, AI-агентов и голосовых ассистентов.

## Основные проекты

- **Kouki.Mugura** — ядро оркестрации команд: реестр, фабрика DTO, валидация, диспетчер, роутеры.
- **Kouki.Shimi** (`Shimi.csproj`) — абстракции окружения (`IFileSystem`, `ITerminal`, `IStateStore`, `ITelemetryLogger`) и подсистема контекстов выполнения.
- **Kouki.Ubusuna** — готовые адаптеры (`ConsoleFileSystem`, `ConsoleTerminal`), `UbusunaBuilder` и DI-расширения.
- **Kouki.Tests** — xUnit-тесты (реестр, фабрика, валидация, роутер, адаптеры, потокобезопасность).
- **Kagedama.api / Kagedama.mcp / Kouki.Kagedama.Bot** — точки входа: HTTP-хост с дашбордом, MCP-сервер для агента, Telegram-бот.

## Документация

- [Архитектура](docs/architecture.md)
- [Датафлоу и типизация](docs/dataflow.md)
- [Руководство по разработке](docs/guides/development.md)
- [Роутеры](docs/guides/routers.md)
- [Примеры](docs/examples/kagedama)
- [API Справочник](api/)

## Быстрый старт

```csharp
services.AddMugura(typeof(PingHandler).Assembly);
services.AddShimiContext();

var router = provider.GetRequiredService<IRouter>();
var result = await router.RouteAsync("ping", null, context);
```

Создайте хендлер с атрибутом `[Command]`, зарегистрируйте через `AddMugura` и вызывайте `IRouter.RouteAsync`. Подробнее — в руководстве по разработке.

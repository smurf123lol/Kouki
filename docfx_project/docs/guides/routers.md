# Роутеры

## Контракт

```csharp
public interface IRouter
{
    Task<JsonNode> RouteAsync(
        string commandName,
        JsonNode? argument,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default);
}
```

`RouteAsync` не выбрасывает исключения, кроме `OperationCanceledException`:
ошибки возвращаются как `{"error": "..."}` (а исключения ниже по стеку — как
`{"error": "...", "type": "<ExceptionType>"}`).

## StaticRouter

Собирается из `AddMugura`, поэтому резолвится как `IRouter`. Порядок обработки:

1. `CommandRegistry.GetEntry(commandName)` → неизвестная команда: `{"error": "Unknown command: ..."}`.
2. `ICommandFactory.TryCreate(entry, argument, out command, out error)` — десериализация JSON в DTO.
3. Контекст: аргумент `context`, иначе `IContextRegistry.GetDefaultContext()`; если контекста нет — `{"error": "No execution context: ..."}`.
4. Валидатор `ICommandValidator<TCommand>` из отдельного DI-скоупа (скоуп закрывается до хендлера).
5. `ICommandDispatcher.SendAsync<TCommand>(command, context, ct)` — хендлер резолвится в скоупе на каждый вызов.

Делегаты `ValidateCore<TCommand>` / `DispatchCore<TCommand>` строятся один раз на тип команды
(`MethodInfo.CreateDelegate`) и кешируются в `ConcurrentDictionary`.

## DelegatingRouter — декоратор

Базовый класс для сквозной логики (логирование, метрики, трассировка, кеш):

```csharp
public sealed class LoggingRouter : DelegatingRouter
{
    private readonly ITelemetryLogger _logger;

    public LoggingRouter(IRouter inner, ITelemetryLogger logger) : base(inner) => _logger = logger;

    public override async Task<JsonNode> RouteAsync(
        string commandName, JsonNode? argument, IExecutionContext? context = null, CancellationToken ct = default)
    {
        _logger.LogEvent("command.started", commandName);
        var result = await base.RouteAsync(commandName, argument, context, ct);
        _logger.LogEvent("command.finished", commandName);
        return result;
    }
}
```

Регистрация: обернуть до `AddMugura` нельзя (там `AddSingleton<IRouter, StaticRouter>()`),
поэтому регистрируйте декоратор после:

```csharp
services.AddMugura(assemblies);
services.AddSingleton<IRouter>(sp => new LoggingRouter(
    new StaticRouter(sp.GetRequiredService<CommandRegistry>(),
                     sp.GetRequiredService<ICommandFactory>(),
                     sp, sp.GetRequiredService<ICommandDispatcher>()),
    sp.GetRequiredService<ITelemetryLogger>()));
```

## Планируемые роутеры

`RemoteRouter` (HTTP/WebSocket) и `MushiRouter` (маршрутизация по домену/интенту)
описаны в `architecture.md`, но в коде ещё не реализованы. Папка `Ubusuna/Routers` — заготовка.

## Контексты

`IExecutionContext` держит `IFileSystem`, `ITerminal`, `IStateStore` и словарь сервисов.
Жизненный цикл задаёт приложение: `IContextRegistry.RegisterContext(name, ctx)` +
`SetDefaultContext(name)`. `UbusunaBuilder` умеет собрать реестр из объявленных контекстов
и назначить контекст по умолчанию.

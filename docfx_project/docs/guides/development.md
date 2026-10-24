# Разработка

## Требования

- .NET 10 SDK (`dotnet --list-sdks` должен показать 10.x).
- Для документации — `docfx` (инструмент ставится отдельно: `dotnet tool install -g docfx`).

## Структура решения

```
Kouki.Mugura/          ядро команд: атрибуты, реестр, фабрика, валидаторы, диспетчер, роутеры
Shimi/                 абстракции среды выполнения и контексты
Ubusuna/               консольные адаптеры и UbusunaBuilder
Kouki.Kagedama*/       прикладной слой, контракты, SQLite-хранилище
Kagedama.api/          HTTP-хост: приём, задачи, журнал, голос, дашборд
Kagedama.mcp/          MCP-сервер stdio для агента
Kouki.Kagedama.Bot/    Telegram-бот
Kouki.Tests/           xUnit-тесты
```

## Как добавить команду

1. DTO — обычный `record`, реализующий `Kouki.Mugura.Commands.ICommand`:

```csharp
public sealed record OpenFileCommand([property: Required] string? Path) : ICommand;
```

2. Обработчик с атрибутом `[Command]` **на самом обработчике** (реестр ищет атрибут на типе,
   реализующем `ICommandHandler<T>`):

```csharp
[Command("open-file", Description = "Открыть файл", Category = CommandCategory.FileOperations)]
public sealed class OpenFileHandler : ICommandHandler<OpenFileCommand>
{
    public Task<JsonNode> HandleAsync(OpenFileCommand command, IExecutionContext? context = null, CancellationToken ct = default)
        => Task.FromResult<JsonNode>(new JsonObject { ["path"] = command.Path });
}
```

3. Зарегистрировать сборку:

```csharp
services.AddMugura(typeof(OpenFileHandler).Assembly);
```

4. Вызвать:

```csharp
var result = await router.RouteAsync("open-file", JsonNode.Parse("""{"path":"README.md"}"""), context);
```

Ограничения DTO проверяются атрибутами DataAnnotations (`[Required]`, `[StringLength]`, `[Range]`).
Валидатор `DataAnnotationsValidator<T>` регистрируется автоматически (`TryAddScoped`).

## Тесты

```bash
dotnet test Kouki.Tests/Kouki.Tests.csproj
```

Покрыто: реестр и атрибуты, фабрика из JSON, валидация, диспетчер и скоупы, роутер
(ошибки/контекст/исключения), `DelegatingRouter`, защита `ConsoleFileSystem` от выхода
за корень, `ConsoleTerminal`, `UbusunaBuilder`.

## Сборка без доступа к nuget.org

Пакеты берутся из локального кеша `~/.nuget/packages`. В ограниченных окружениях
(например, песочница без named pipes) многонодовый MSBuild падает молча — используйте:

```bash
dotnet restore <project> -m:1 -nodeReuse:false
dotnet build <project> --no-restore -m:1 -nodeReuse:false
```

`dotnet test` в такой песочнице может не запуститься (VSTest testhost вызывает
`OpenProcess`); обходной путь — раннер, вызывающий `[Fact]`-методы через рефлексию.

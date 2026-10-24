# Как контрибьютить в Kouki

Коротко: .NET 10, одна SQLite-база, три хоста. Правила контрактов — в [AGENTS.md](AGENTS.md)
(их читают и люди, и агенты); здесь — практика: как собрать, проверить и что считается готовым.

## Быстрый старт

```powershell
git clone <repo> Kouki
cd Kouki
dotnet restore Kouki.slnx -m:1 -nodeReuse:false
dotnet build   Kouki.slnx --no-restore -m:1 -nodeReuse:false
```

Нужен .NET SDK 10.0 (см. `global.json`). NuGet-пакеты — из локального кеша, интернет не обязателен.

### Поднять сервер

```powershell
# без распознавания речи — быстрый цикл разработки
dotnet run --project Kagedama.api --urls http://127.0.0.1:5300

# с распознаванием (sherpa-onnx, model в models\asr) — всё через один скрипт
$env:KOUKI_TOKEN = 'ваш-токен'; tools\run-server.ps1 -Port 5300
```

Токен обязателен: без него сервер не стартует (fail-closed). Для локальной отладки можно
задать `Kouki:AllowAnonymous=true` (env `Kouki__AllowAnonymous=true` или свой
`appsettings.Development.json`), но в коммит это не тащим.

### Тесты

```powershell
# обычный путь (на машине без песочницы)
dotnet test Kouki.Tests\Kouki.Tests.csproj -m:1 -nodeReuse:false

# так гоняются тесты в ограниченном окружении (dotnet test там падает на OpenProcess)
dotnet restore _scratch\testrun\testrun.csproj -m:1 -nodeReuse:false
dotnet build   _scratch\testrun\testrun.csproj --no-restore -m:1 -nodeReuse:false
dotnet _scratch\testrun\bin\Debug\net10.0\Kouki.Tests.Runner.dll
```

Тесты пишем `[Fact]`. `[Theory]` в самописном раннере не исполняется — цикл внутри `[Fact]`.

## Карта проектов

| Проект | Ответственность |
| --- | --- |
| `Kouki.Shimi` | контракты окружения: файлы, терминал, состояние, речь (`ISpeechTranscriber`) |
| `Kouki.Mugura` | команды: атрибуты, реестр, фабрика, диспетчер, роутеры, подтверждения |
| `Kouki.Un` | распознавание речи (sherpa-onnx + заглушка), включается флагом сборки |
| `Kouki.Ubusuna` | консольные адаптеры и конструктор контекста |
| `Kouki.Yasabi` | классификация фразы (правила), журнал вердиктов, `IIntentRouter` |
| `Kouki.Kagedama.Contracts` | интерфейсы данных, статусы, приоритеты, источники, исходы |
| `Kouki.Kagedama.Data` | SQLite: сессия, единица работы, четыре хранилища, схема |
| `Kouki.Kagedama` | приём фразы, команды задач, аудит действий, сборка DI хостов |
| `Kagedama.api` | HTTP + дашборд (`wwwroot`) |
| `Kagedama.mcp` | MCP-сервер по stdio для агентов |
| `Kouki.Kagedama.Bot` | Telegram-бот |
| `tools\Kouki.SpeechLab` | синтез/распознавание для проверок без микрофона |

## Как добавить команду

Команда живёт в проекте-потребителе, а не в `Kouki.Mugura`: ядро — это библиотека, которую
можно взять в приложение, где нет ни задач, ни SQLite. В этом репозитории потребитель —
`Kouki.Kagedama`; в отдельном продукте это будет ваш собственный проект со ссылкой на
`Kouki.Mugura` (и, если нужен разбор фразы, на `Kouki.Yasabi`). Менять ради новой команды
`Kouki.Mugura` не нужно.

1. Команда (DTO) + обработчик рядом, в `Kouki.Kagedama\Commands\`:

```csharp
public sealed record CountTasksCommand(string? Status = null) : ICommand;

[Command("count_tasks", Description = "Считает задачи по статусу", Category = CommandCategory.Navigation)]
public sealed class CountTasksHandler(ITaskStore store) : ICommandHandler<CountTasksCommand>
{
    public async Task<JsonNode> HandleAsync(CountTasksCommand command, IExecutionContext? context = null, CancellationToken ct = default)
        => new JsonObject { ["count"] = (await store.ListAsync(command.Status, 500, ct)).Count };
}
```

2. Валидация — атрибутами на свойствах DTO (`[Required]`, `[Range]`), не в обработчике.
3. Тест рядом с потребителем (здесь — `Kouki.Tests`): хотя бы «команда видна в реестре и
   возвращает ожидаемый JSON».
4. Если команда меняет состояние — `RequiresConfirmation = true` и реализация `ICommandConfirmation` в хосте.
5. Если она должна быть доступна агенту — дескриптор в `Kagedama.mcp\KoukiMcpTools.cs`.

## Definition of done

- [ ] `dotnet build Kouki.slnx` — 0 ошибок (предупреждения NU1900 в офлайне допустимы).
- [ ] Тесты зелёные; новые тесты есть на каждый новый контракт.
- [ ] Нет новых литералов статусов/источников/приоритетов — только `*Statuses`/`TaskSources`/`TaskPriorities`.
- [ ] Запись в БД — через хранилища и `IUnitOfWork`, если таблиц больше одной.
- [ ] Обновлены `README.md` и, при смене контрактов, `docfx_project/docs/*`.
- [ ] В заметках воркспейса отмечен статус задачи (`notes-updated\Задачи Kouki.md`).
- [ ] В индекс не попали `bin/`, `obj/`, `models/`, `*.sqlite`, аудио.

## Логи и отладка

- HTTP-запросы: `Microsoft.AspNetCore.HttpLogging` (метод, путь, статус, длительность).
- Приём фразы пишет строку в `actions` (тип, команда, исход, длительность, ошибка) — это первый
  источник при разборе «почему задача не создалась».
- Журнал разбора (`classifications`) — второй: там вердикт классификатора, подсказка сессии,
  уверенность и текст. Он же датасет — не затирай его.
- Если сервер собрался без флага sherpa, в лог придёт предупреждение с точной командой пересборки.

## Коммиты

Формат: `тип(область): короткое описание` — `fix(mic)`, `feat(tasks)`, `docs(agents)`, `test(mcp)`.
Один коммит — одно изменение поведения. Сгенерированные файлы и стенды в коммит не входят.
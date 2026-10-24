### 1. Общая концепция

Kouki — модульная платформа для оркестрации команд. Её встраивают в AI-агенты, голосовые ассистенты, CLI, веб-сервисы и автоматизированные системы: внешняя оболочка (MCP-сервер, VS-расширение, голосовой ассистент) передаёт имя команды и JSON-аргументы, а ядро находит хендлер, валидирует аргументы и выполняет его в изолированном DI-скоупе с доступом к окружению (файловая система, терминал, состояние).

Репозиторий состоит из четырёх проектов:

- **Kouki.Mugura** (сборка `Kouki.Mugura.dll`) — чистое ядро: реестр команд, фабрика DTO, валидация, диспетчер, роутеры. Не зависит от окружения.
- **Kouki.Shimi** (проект `Shimi.csproj`, `AssemblyName`/`RootNamespace` = `Kouki.Shimi`) — абстракции окружения и подсистема контекстов выполнения.
- **Kouki.Ubusuna** (`Kouki.Ubusuna.dll`) — готовые адаптеры, `UbusunaBuilder` и DI-расширения.
- **Kagedama.api / Kagedama.mcp / Kouki.Kagedama.Bot** — точки входа: HTTP-хост с дашбордом, MCP-сервер для агента и Telegram-бот.

Такое разделение даёт чистое, тестируемое ядро, подменяемые адаптеры и независимые точки входа.

---

### 2. Mugura (ядро)

#### 2.1. Назначение

- Регистрация команд атрибутом `[Command]` на классе-хендлере.
- Оркестрация вызова: имя команды → `CommandEntry` → DTO → валидация → хендлер.
- Изоляция зависимостей: каждый вызов выполняется в своём DI-скоупе.
- Единый формат ввода/вывода: аргумент — `JsonNode?`, результат — `JsonNode`.
- Валидация DTO через `System.ComponentModel.DataAnnotations` с возможностью замены валидатора.

#### 2.2. Ключевые компоненты

- **`CommandAttribute`** (`Kouki.Mugura.Attributes`) — ставится на класс-хендлер, а не на DTO. Поля: `Name`, `Description`, `RequiresConfirmation`, `Category`, `Example`.
- **`CommandCategory`** — `[Flags]`-enum, значения `1 << n`: `Uncategorized`, `Navigation`, `FileOperations`, `Terminal`, `Environment`, `Git`, `Dangerous`, `Admin`, `Integration`.
- **`CommandRegistry`** — сканирует переданные сборки, находит типы с `[Command]`, реализующие `ICommandHandler<>`, и запоминает `CommandEntry`. Извлекает `[Required]`-подобные правила из свойств DTO. `Names` возвращает имена команд.
- **`CommandEntry`**, **`ValidationRule`** — неизменяемые записи: тип DTO, тип хендлера, метаданные атрибута и правила валидации.
- **`ICommandFactory`** — создаёт DTO из `JsonNode`: `Create(entry, argument)` и `TryCreate(entry, argument, out command, out error)`. Реализация `CommandFactory` использует `JsonSerializer.Deserialize` с `JsonSerializerDefaults.Web`; `null` нормализуется в `{}`.
- **`ICommandValidator<TCommand>`** (`Kouki.Mugura.Validators`) — валидатор DTO; реализация по умолчанию `DataAnnotationsValidator<TCommand>` проверяет свойства через правила из `CommandEntry`. Регистрируется открытым generic'ом в `AddMugura`.
- **`IScopeManager` / `ScopeManager`** — создаёт `AsyncServiceScope` на вызов.
- **`ICommandDispatcher` / `CommandDispatcher`** — `SendAsync<TCommand>(command, context, ct)`: проверяет аргументы, открывает скоуп, резолвит `ICommandHandler<TCommand>` и вызывает `HandleAsync`.
- **`IRouter`** — `RouteAsync(commandName, argument, context?, ct)`.
- **`StaticRouter`** — основная реализация: реестр → фабрика → контекст (переданный или по умолчанию из `IContextRegistry`) → валидатор из скоупа → диспетчер. Делегаты валидатора и диспетчера кешируются по типу команды (`Delegate.CreateDelegate`, без `MethodInfo.Invoke`).
- **Граница ошибок** — весь конвейер `StaticRouter` обёрнут в `try/catch`: исключение превращается в `{"error": ..., "type": ...}`, `OperationCanceledException` пробрасывается.
- **`DelegatingRouter`** — абстрактный базовый класс для роутеров-обёрток: принимает `IRouter`, переопределяемый `RouteAsync` делегирует вызов.
- **`AddMugura(params Assembly[])`** — регистрирует синглтоны ядра, `ICommandValidator<> → DataAnnotationsValidator<>` и все хендлеры как Scoped; пустой список сборок → вызывающая сборка.

#### 2.3. Поток вызова

1. Внешний код вызывает `IRouter.RouteAsync(commandName, argument, context?)`.
2. `StaticRouter` находит `CommandEntry`; неизвестное имя → `{"error": "Unknown command: ..."}`.
3. `ICommandFactory.TryCreate` десериализует аргумент; ошибка формы → `{"error": ...}`.
4. Выбирается контекст: аргумент или `IContextRegistry.GetDefaultContext()`; если нет — `{"error": "No execution context: ..."}`.
5. Валидатор резолвится из свежего скоупа; при нарушении правил → `{"error": ...}`.
6. `ICommandDispatcher.SendAsync` открывает скоуп, получает хендлер и вызывает `HandleAsync`.
7. Хендлер возвращает `JsonNode`; любое исключение на шагах 2–7 кроме отмены превращается в `{"error", "type"}`.

---

### 3. Shimi (абстракции и контексты)

#### 3.1. Абстракции (`Kouki.Shimi.ServiceAbstractions`)

- **`IFileSystem`** — `ReadBytesAsync`, `WriteBytesAsync`, `ExistsAsync`, `GetTreeAsync`.
- **`ITextFileSystem : IFileSystem`** — `ReadTextAsync`, `WriteTextAsync`, `AppendTextAsync` (`Encoding?`).
- **`IJsonFileSystem : IFileSystem`** — `ReadJsonAsync<T>`, `WriteJsonAsync<T>` (`JsonSerializerOptions?`).
- **`ITerminal`** — `RunCommandAsync(command, workingDirectory?, ct)`.
- **`IStateStore`** — `GetAsync<T>`, `SetAsync<T>`, `RemoveAsync`.
- **`ITelemetryLogger`** — `LogEvent`, `LogError`.

#### 3.2. Контексты (`Kouki.Shimi.Context`)

- **`IExecutionContext`** — `Name`, `FileSystem`, `Terminal`, `StateStore`, `GetService<T>()`.
- **`BaseExecutionContext`** — обычный класс (не абстрактный): принимает имя и три сервиса, отдаёт дополнительные сервисы через `GetService<T>()`.
- **`IContextRegistry`** / **`ContextRegistry`** — потокобезопасный реестр: `ConcurrentDictionary` + `Volatile` для контекста по умолчанию; `GetContext`, `RegisterContext`, `SetDefaultContext` (по экземпляру и по имени), `GetDefaultContext`, `TryGetDefaultContext(out ...)`.
- **`InMemoryStateStore`** — потокобезопасное хранилище в памяти.
- **`AddShimiContext`** (`Kouki.Shimi.DependencyInjection`) — `TryAdd`-регистрация `IContextRegistry` и `IStateStore`.

`Shimi` **не создаёт** контекст по умолчанию автоматически: его задаёт приложение (`SetDefaultContext`) или `UbusunaBuilder`.

---

### 4. Ubusuna (библиотека расширений)

#### 4.1. Адаптеры (`Kouki.Ubusuna.Adapters`)

- **`ConsoleFileSystem`** — реализует `IFileSystem`, `ITextFileSystem`, `IJsonFileSystem`; корень задаётся в конструкторе (по умолчанию — текущий каталог), пути нормализуются и проверяются на выход за корень (`UnauthorizedAccessException`), `CancellationToken` учитывается.
- **`ConsoleTerminal`** — `ProcessStartInfo.ArgumentList`, `cmd.exe /c` на Windows и `/bin/sh -c` на Unix; при ненулевом коде выхода бросает `InvalidOperationException` с кодом и stderr.
- **`HttpFileSystem`**, **`FtpFileSystem`** — планируются.

#### 4.2. Сборка конфигурации

- **`UbusunaBuilder`** (`Kouki.Ubusuna`) — fluent-конфигурация: `UseConsoleAdapters()`, `AddContext(name, factory)`, `SetDefaultContext(name)`, `ConfigureServices(...)`, финальный `Build()`. Регистрирует `IContextRegistry` фабрикой с реальным `IServiceProvider`, наполняет реестр объявленными контекстами и при отсутствии явного — создаёт консольный контекст по умолчанию. Повторный `Build()` бросает `InvalidOperationException`.
- **`AddUbusuna(Action<UbusunaOptions>?)`** (`Kouki.Ubusuna.DependencyInjection`) — `TryAdd`-регистрация адаптеров при `UseConsoleAdapters`.
- **`PingCommand` / `PingHandler`** — эталонная команда `ping` (возвращает `"pong"`).

#### 4.3. Роутеры

- `DelegatingRouter` живёт в ядре (`Kouki.Mugura.Dispatcher`) — им пользуются все расширения.
- `RemoteRouter` (сеть) и `MushiRouter` (композиция нескольких роутеров) **не реализованы**; `IRouter` и `DelegatingRouter` — готовая точка расширения.

---

### 5. Приложения (Kagedama)

- **Kagedama.api** — HTTP-хост: приём текста и голоса, задачи, журнал классификаций, действия, `/api/context`, дашборд (`wwwroot`, без сборки фронтенда).
- **Kagedama.mcp** — MCP-сервер stdio (семь инструментов `kouki_*`) для агента DeepSeek Harness; лог идёт в stderr, чтобы не мешать JSON-RPC.
- **Kouki.Kagedama.Bot** — Telegram-бот: long polling, фраза уходит в приём, ответ возвращается в чат.

Расширение Visual Studio (`Kagedama.vs`) из решения убрано: проект таргетил `net8.0-windows8.0` и требовал VS SDK, то есть ломал сборку на Linux. Возвращается отдельной задачей в MVP.

---

### 6. Взаимодействие с голосовым ассистентом (IntentRouter)

`IntentRouter` — внешняя реализация `IRouter`, наследник `DelegatingRouter`. Он принимает голосовую фразу вместо имени команды, определяет интент каскадом (ML.NET/fastText → BERT → LLM или уточнение), извлекает слоты, собирает `JsonNode` аргументов и вызывает `base.RouteAsync` с именем команды. В коде проекта его нет.

---

### 7. Преимущества разделения

- **Чистое ядро** — `Mugura` не знает про файловую систему и процессы, тестируется без окружения.
- **Подменяемые адаптеры** — `Shimi` определяет контракты, `Ubusuna` даёт консольные реализации, приложение — свои (Obsidian, WinAPI, LiteDB).
- **Контексты** — переключение окружения (пользователь, проект, сессия) без изменения хендлеров.
- **Единый протокол** — `JsonNode` на входе и выходе упрощает интеграцию с MCP/HTTP/голосом.
- **DI-дружественность** — всё собирается стандартным контейнером, `TryAdd`-регистрация не конфликтует с пользовательскими переопределениями.

---

### 8. Рекомендации по использованию

1. Подключить `Kouki.Mugura`, `Kouki.Shimi` (проект `Shimi.csproj`) и, при необходимости, `Kouki.Ubusuna`.
2. В DI вызвать `AddMugura(typeof(MyHandler).Assembly)`; хендлеры помечать `[Command]`.
3. Для окружения — `AddShimiContext()` либо `new UbusunaBuilder(services).UseConsoleAdapters().SetDefaultContext("console").Build()`.
4. В точке входа (MCP, CLI, HTTP, голос) вызвать `IRouter.RouteAsync(commandName, argument, context?)`.
5. Нужна сквозная логика (логирование, метрики, авторизация) — унаследоваться от `DelegatingRouter` и зарегистрировать декоратор после `AddMugura`.

---

### 9. Тестирование и сборка

- `Kouki.Tests` (xUnit, подключён в `Kouki.slnx`, папка `/Tests/`) покрывает реестр, фабрику, валидацию, диспетчер, `StaticRouter`, `DelegatingRouter`, адаптеры, `UbusunaBuilder` и потокобезопасность Shimi.
- CI: `.github/workflows/ci.yml` — `dotnet restore` → `dotnet build` → `dotnet test`.
- Общие настройки сборки — `Directory.Build.props`.
- Офлайн-сборка в ограниченной песочнице: `dotnet restore <proj> -m:1 -nodeReuse:false`, затем `dotnet build <proj> --no-restore -m:1 -nodeReuse:false`.

---

### 10. Что не входит в эту заметку

- Точные сигнатуры методов — в разделе «Датафлоу и типизация».
- Детали реализации `CommandRegistry` (рефлексия против генерации кода).
- Устройство `ScopeManager`.
- ML-модели для `IntentRouter`.

### 11. Сервер, бот, MCP и дашборд (этапы M1–M5)

Разделы 1–10 описывают ядро. Прикладная часть выросла вокруг него:

| Проект | Роль | Ключевые зависимости |
| --- | --- | --- |
| `Kouki.Kagedama.Data` | SQLite-хранилище: `ITaskStore`, `IClassificationJournal`, `IActionLog`, `DatabaseInitializer` | Dapper, Microsoft.Data.Sqlite |
| `Kouki.Kagedama` | прикладной слой: `TaskIntakeService`, команды `list_tasks`/`complete_task`, `IntakeOptions` | Mugura, Yasabi, Data |
| `Kagedama.api` | ASP.NET Core: HTTP API, дашборд (`wwwroot`), bearer-токен | Kagedama, Data |
| `Kagedama.mcp` | MCP-сервер поверх stdio: шесть инструментов `kouki_*` | Kagedama, Data |
| `Kouki.Kagedama.Bot` | Telegram long polling | HTTP API (без пакетов) |
| `dsh-kouki` | плагин DSH: MCP-клиент, регистрирует инструменты в агенте | `@deepseek-ai/cordis` |

Все входы — Telegram, ESP32-станция, дашборд, агент через MCP — сходятся в один путь:
`TaskIntakeService.IntakeAsync` → `Kouki.Yasabi` (классификация) → `StaticRouter` (исполнение)
→ `Kouki.Kagedama.Data` (задача + журнал классификаций + журнал действий). Подробности
в `docs/dataflow.md` (разделы 16–24) и в `README.md`.

Дашборд — статика без сборки (`Kagedama.api/wwwroot`): токен закрывает `/api/*`, страница и
ассеты открыты, чтобы браузер мог загрузить интерфейс.
### 12. Контракты, состояние и сервисы контекста

Интерфейсы слоя данных вынесены в `Kouki.Kagedama.Contracts` (без зависимостей):
`ITaskStore`, `IActionLog`, `IClassificationJournal` и их записи. Реализации остались в
`Kouki.Kagedama.Data`. Это разрывает связь «Ясаби → SQLite»: понимание языка больше не
тянет за собой Dapper.

`IStateStore` реализован дважды:

| Реализация | Где | Когда |
| --- | --- | --- |
| `InMemoryStateStore` | `Shimi/Context` | тесты, консольные контексты |
| `SqliteStateStore` | `Kouki.Kagedama.Data` | сервер и MCP — переживает перезапуск |

`SqliteStateStore` хранит JSON в таблице `state (key, value, updated_at)` с upsert; для
отсутствующего ключа возвращается `default(T)`.

Сервисы контекста стали явными: `IExecutionContext` объявляет default-члены
(`Services`, `GetService<T>`, `TryGetService<T>`, `ServiceTypes`), `BaseExecutionContext`
разрешает сервис сначала из словаря `AddService`, затем из `IServiceProvider`, затем `null`.
`GetRequiredService<T>(usedFor)` в `Kouki.Shimi.Context` бросает исключение с подсказкой,
что именно и как добавить.

Диагностика: `GET /api/context` и MCP `kouki_context` возвращают имя контекста, список
известных типов сервисов, имена реализаций файловой системы/терминала/хранилища и признак
наличия `IServiceProvider`.
### 13. Голос: WAV и распознавание

`POST /api/voice` принимает WAV целиком — так его отдаёт станция на ESP32-S3 (фраза по кнопке, без wake-word). Формат один: PCM 16 бит, моно, 16 кГц; всё остальное — `415` с перечислением того, что пришло, потому что молчаливое пересэмплирование скрыло бы ошибку клиента.

Порядок: `WavReader.TryParse` → `ISpeechTranscriber.TranscribeAsync` → `FileAudioStore.SaveAsync` (если `StoreAudio`) → `VoiceIntakeService.AcceptAsync` → `TaskIntakeService.IntakeAsync(text, "voice", asrConfidence, audioPath, intentHint)` → запись в `actions`. Коды: `413` больше `MaxVoiceBytes` (5 МБ) или `MaxVoiceSeconds` (30 с), `415` не тот формат, `422` речь не распознана, `503` распознавание не подключено.

Интент сообщает сессия, а не текст. Станция шлёт `POST /api/voice?source=station&intent=add_task` — это значит «открыт режим записи», и фраза становится задачей целиком: из неё ничего не вырезается. Отдельного «слоя понимания императива» нет намеренно — регулярка не отличает «Запиши задачу купить хлеб» от «Задача проекта — сдать отчёт», а вычитание слов без этого слоя портит фразу необратимо. `TaskIntakeService.IntakeAsync(..., string? intentHint)` перекрывает результат классификатора (`intent with { Intent = hint, Score = 1, Model += "+session" }`), журнал классификации всё равно пишется. Без подсказки фраза идёт через классификатор, а если он не уверен — уходит в разбор целиком. То же поле (`"intent"`) принимает `/api/intake`.

Порог один, и он про смысл. `IntakeOptions.ConfidenceThreshold` (0.75) отвечает на вопрос «поняли ли фразу»; отдельного порога на уверенность распознавания нет. Причин две: sherpa-onnx вероятность не отдаёт (в `TranscriptionResult.Confidence` приходит `null`), а решение «на разбор или нет» всё равно должен принимать классификатор интента — плохо расслышанная, но понятая команда полезнее, чем разбор ради самой уверенности. Если модель когда-нибудь начнёт отдавать вероятность, она попадёт в `meta.asrConfidence` и `classifications.asr_confidence` как метаданные, но маршрут не изменит.

Распознавание — `sherpa-onnx` (Whisper, офлайн). Пакет подключается флагом `-p:KoukiEnableSherpa=true`, потому что тянет нативные библиотеки под RID; без флага собирается заглушка `UnavailableSpeechTranscriber`, сервер работает, `/api/voice` отвечает `503` с причиной. Модель не в репозитории: `scripts/fetch-asr-model.ps1` скачивает `whisper-small` с GitHub releases, путь задаётся `Kouki__SpeechModelDirectory`. Загрузка ленивая: ошибка «модель не найдена» приходит на первую фразу, а не при старте.

Проверять голос без микрофона помогает `tools/Kouki.SpeechLab`: `roundtrip` синтезирует русскую фразу (`scripts/fetch-tts-model.ps1`, голос VITS/Piper), прогоняет её через тот же `ISpeechTranscriber` и считает совпадение текста. Это проверка конвейера — «фраза доходит и понимается», а не замер качества модели; датасет для обучения так не собирают.

Уверенность sherpa-onnx не отдаёт, поэтому `TranscriptionResult.Confidence` равен `null`, и это трактуется как «неизвестно», а не «низкая» — иначе на разбор уходила бы каждая фраза. Когда появится модель с вероятностью, место готово: тип `double?`, колонка `classifications.asr_confidence`, аудио сохраняется до разметки.
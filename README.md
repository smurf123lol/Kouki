# Kouki

Командный слой для приложений: команды описываются DTO, обработчики — обычные классы,
валидация — DataAnnotations, транспорт (MCP/HTTP/VS) подключается роутером.

## Модули

| Проект | Что делает |
| --- | --- |
| `Kouki.Mugura` | Ядро: `[Command]`, `CommandRegistry`, `ICommandFactory`, `ICommandValidator<T>`, `ICommandDispatcher`, `IRouter` / `StaticRouter` / `DelegatingRouter`. |
| `Shimi` (сборка `Kouki.Shimi`) | Абстракции среды выполнения: `IFileSystem`, `ITextFileSystem`, `IJsonFileSystem`, `ITerminal`, `IStateStore`, `ITelemetryLogger`, `ISpeechTranscriber`, `IExecutionContext`, `IContextRegistry`. |
| `Kouki.Un` | Слух: `ISpeechTranscriber` и `SherpaSpeechTranscriber` (sherpa-onnx, Whisper, офлайн) под флагом сборки; без флага — заглушка `UnavailableSpeechTranscriber`. |
| `Kouki.Yasabi` | Понимание: правила интентов (`RuleIntentClassifier`), журнал low-confidence (`IClassificationJournal`), маршрут «фраза → команда» (`IIntentRouter`). |
| `Kouki.Ubusuna` | Платформенный слой: консольные адаптеры (`ConsoleFileSystem`, `ConsoleTerminal`) и `UbusunaBuilder` для регистрации контекстов. |
| `Kouki.Kagedama` | Прикладной слой: `TaskIntakeService`, `IntakeOptions`, команды `list_tasks` / `complete_task`. Живёт вне транспортов. |
| `Kouki.Kagedama.Contracts` | Интерфейсы слоя данных без зависимостей: `ITaskStore`, `IActionLog`, `IClassificationJournal` и их записи. |
| `Kouki.Kagedama.Data` | SQLite-хранилище: `SqliteTaskStore`, `SqliteClassificationJournal`, `SqliteActionLog`, `SqliteStateStore`, `DatabaseInitializer`. |
| `Kagedama.api` | HTTP-хост `Kouki.Kagedama.Api`: приём задач, задачи, журнал, bearer-токен. |
| `Kagedama.mcp` | MCP-сервер stdio: семь инструментов `kouki_*` для агента DeepSeek Harness. |
| `Kouki.Kagedama.Bot` | Telegram-бот: длинный опрос Bot API, отправка фраз в приём, ответ в чат. |
| `dsh-kouki/` | Плагин DeepSeek Harness (JS, без внешних зависимостей): MCP-клиент и те же инструменты. |
| `Kouki.Tests` | xUnit-тесты ядра, роутера, адаптеров, «Ясаби», сервера и бота. |

## Быстрый старт

```csharp
using Kouki.Mugura.Attributes;
using Kouki.Mugura.Commands;
using Kouki.Mugura.Dispatcher;
using Kouki.Mugura.DependencyInjection;
using Kouki.Mugura.Handlers;
using Kouki.Shimi.Context;
using System.Text.Json.Nodes;

public sealed record PingCommand : ICommand;

[Command("ping", Description = "Проверка связи")]
public sealed class PingHandler : ICommandHandler<PingCommand>
{
    public Task<JsonNode> HandleAsync(PingCommand command, IExecutionContext? context = null, CancellationToken ct = default)
        => Task.FromResult<JsonNode>(JsonValue.Create("pong"));
}

var services = new ServiceCollection();
services.AddMugura(typeof(PingHandler).Assembly);

using var provider = services.BuildServiceProvider();
var router = provider.GetRequiredService<IRouter>();

var result = await router.RouteAsync("ping", null, context);
```

`IExecutionContext` передаётся явно либо берётся из `IContextRegistry`
(зарегистрировать: `services.AddShimiContext()` + `registry.SetDefaultContext(...)`).

## Сервер задач (M1)

`Kagedama.api` принимает фразы с телефона, станции или дашборда, понимает интент через
«Ясаби» и отдаёт команду диспатчеру; в SQLite `kouki.sqlite` пишутся задачи (только `add_task`
и фразы «на разбор»), журнал распознавания и журнал действий.

```bash
dotnet run --project Kagedama.api            # http://localhost:5000
curl -X POST http://localhost:5000/api/intake \
     -H "Content-Type: application/json" \
     -d '{"text":"запиши задачу купить хлеб","source":"telegram"}'
```

Эндпоинты: `GET /api/health` (публичный), `POST /api/intake`, `GET /api/tasks`,
`GET|PATCH /api/tasks/{id}`, `GET /api/classifications`, `POST /api/classifications/{id}/label`,
`GET /api/actions?taskId=&limit=`, `GET /api/context`, `POST /api/voice` (WAV, см. раздел «Голос»),
`GET /api/classifiers`, `POST /api/classify` (сравнение роутеров), `POST /api/transcribe` (только WAV → текст).
Авторизация — `Authorization: Bearer <Kouki:Token>`; пустой токен отключает проверку.

Настройки — секция `Kouki` в `appsettings.json`: `DatabasePath` (`kouki.sqlite`), `Token`,
`ConfidenceThreshold` (0.75), `StoreAudio`, `AudioDirectory`.

Журнал `classifications` пишет **все** классификации, включая низкую уверенность и ошибки, —
это датасет для будущего BERT; нераспознанные фразы попадают в задачи со статусом `needs_review`.

## Telegram-бот (M2)

`Kouki.Kagedama.Bot` — отдельный проект: длинный опрос Bot API, отправка текста в
`POST /api/intake` и короткий ответ в чат. Сторонних пакетов не требует.

```bash
cd Kouki.Kagedama.Bot
dotnet run -- --bot-token=123:ABC --server-url=http://127.0.0.1:5199
```

Токены можно задать в `appsettings.json` (секция `KoukiBot`), переменными окружения
`KOUKI_BOT_TOKEN` / `KOUKI_SERVER_URL` / `KOUKI_SERVER_TOKEN` или аргументами `--bot-token=`,
`--server-url=`, `--server-token=`.

Команды задач (M2):

| Интент | Команда | Что делает |
|---|---|---|
| `add_task` | `add_task` | создаёт задачу через диспатчер, `outcome = created` |
| `list_tasks` | `list_tasks` | возвращает последние задачи, ничего не меняя |
| `complete_task` | `complete_task` | закрывает задачу из слота `taskId`; без слота — последнюю открытую |

## MCP и плагин `dsh-kouki` (M3)

`Kagedama.mcp` — MCP-сервер поверх stdio (`initialize`, `tools/list`, `tools/call`):
семь инструментов `kouki_add_task`, `kouki_list_tasks`, `kouki_complete_task`,
`kouki_unresolved_classifications`, `kouki_label_classification`, `kouki_task_history`,
`kouki_context`.
Логи пишутся в stderr, в stdout — только JSON-RPC.

```bash
dotnet build Kagedama.mcp/Kagedama.mcp.csproj -m:1 -nodeReuse:false
dotnet Kagedama.mcp/bin/Debug/net10.0/Kagedama.mcp.dll --database=С:\kouki\kouki.sqlite
```

Плагин `dsh-kouki` поднимает этот процесс как MCP-клиент и регистрирует эти инструменты
в агенте:

```bash
dsh plugin --profile kouki add E:\путь\к\Kouki\dsh-kouki
```

Конфиг плагина: `mcpCommand` (`dotnet`), `mcpArgs` (путь к `Kagedama.mcp.dll`),
`databasePath`, `cwd`, `requestTimeoutMs`. Тесты клиента: `node dsh-kouki/test/mcp-client.test.mjs`
(эмулятор транспорта — реальный `spawn` в песочнице запрещён).

## Станция (M4.3)

Голосовая станция на ESP32-S3 живёт отдельным репозиторием — `kouki-station`
(рядом с этим): прошивка ESP-IDF, запись WAV по кнопке, отправка на
`POST /api/voice`. Здесь, на сервере, для неё уже есть всё: приём WAV,
распознавание sherpa-onnx, интент, команды, журнал. Сквозная проверка голоса
без платы — `tools/Kouki.SpeechLab` (`roundtrip` синтезирует фразу и прогоняет
её через тот же распознаватель).

**Проверено на живой плате:** прошивка собрана ESP-IDF 5.5.1, залита в
`ESP32-S3-Touch-LCD-1.85C` (COM3), плата поднимает Wi-Fi и шлёт запись на
сервер; сервер отвечает, кладёт фразу в разбор и сохраняет WAV в `audio/`.
## Состояние и контекст (M5.1)

`IStateStore` теперь переживает перезапуск: `SqliteStateStore` пишет JSON в таблицу `state`
той же базы (`key`, `value`, `updated_at`, upsert). Раньше это был `InMemoryStateStore`,
и «память» жила до остановки процесса.

Контекст получил явный доступ к сервисам:

```csharp
var context = new BaseExecutionContext("server", fileSystem, terminal, stateStore, services);
context.AddService<IClock>(clock);              // привязка «здесь и сейчас»
var clock2 = context.GetRequiredService<IClock>("в логе");  // падает с подсказкой, если нет
```

Порядок разрешения: `AddService` → `IServiceProvider`, переданный в конструктор → `null`.
Три «железных» сервиса (`IFileSystem`, `ITerminal`, `IStateStore`) удалить нельзя.
Посмотреть, что видит конкретный контекст: `GET /api/context` или MCP `kouki_context`.

Интерфейсы слоя данных вынесены в `Kouki.Kagedama.Contracts`, поэтому `Kouki.Yasabi` больше
не тянет SQLite, а `Kouki.Kagedama.Data` зависит от контрактов и Shimi. Хранилище подключается
одной строкой — `services.AddKagedamaData(path)` — и регистрирует задачи, оба журнала и состояние.
## Дашборд (M5)

`Kagedama.api/wwwroot` — дашборд без сборки и без npm: три файла (`index.html`,
`styles.css`, `app.js`) и SVG-иконка. Внешний вид намеренно не «админка»: тёплая
бумажно-чернильная палитра, вертикальный корешок с названием, плитки-таблички со
счётчиками и журнальная вёрстка списка (волосяные линейки, статус в отступе).

Как запустить:

```bash
# токен берётся из конфигурации Kouki:Token (или Kouki__Token), по умолчанию пустой = без токена
dotnet Kagedama.api/bin/Debug/net10.0/Kagedama.api.dll --urls http://127.0.0.1:5199
# открыть http://127.0.0.1:5199/ и вставить токен в поле «bearer-токен»
```

Что есть на экране: счётчики задач/разбора/действий, список задач с фильтрами по статусу,
карточка задачи с таймлайном действий (кто, когда, сколько миллисекунд), разбор уверенности
с разметкой прямо из браузера, ручной приём фразы как «будто из источника», журнал действий
и вкладка «Проверка»: одна фраза через все классификаторы (таблица «интент / уверенность /
порог / модель») плюс загрузка WAV — файл распознаётся через `POST /api/transcribe` и сразу
уходит в сравнение. Это и есть «прогон через разные роутеры»: новый классификатор появляется
в списке сам, как только его зарегистрировали в `AddYasabi`.

Токен закрывает `/api/*` (кроме `/api/health`), страница и статика открыты — иначе браузер
не смог бы загрузить сам дашборд. Токен хранится в `localStorage`.

Тесты статики: `Kouki.Tests/DashboardAssetsTests.cs` — проверяют наличие файлов, дизайн-токены
в CSS и то, что `app.js` не обращается к несуществующим `id`.

## Голос (M4)

`POST /api/voice` принимает WAV **целиком** (PCM 16 бит, моно, 16 кГц) — ровно в таком виде его
отдаёт станция на ESP32-S3, поэтому серверу не нужен ни `multipart`, ни потоковый разбор.
Ответ — тот же приём, что и у текста: распознанный текст, интент, команда и её результат.

```bash
curl -X POST --data-binary "@phrase.wav" \
     -H "Authorization: Bearer $KOUKI_TOKEN" \
     -H "Content-Type: audio/wav" \
     "http://127.0.0.1:5199/api/voice?source=station&intent=add_task"
```

Параметр `intent` — это **сессия**: станция сообщает, что пользователь открыл режим записи, и фраза
становится задачей целиком, без попыток вырезать из неё «запиши задачу». Из фразы ничего не удаляется
никогда: без подсказки её разбирает классификатор, а если он не уверен — фраза целиком уходит на разбор.
`/api/intake` принимает то же самое полем `"intent"` в теле.

Порядок внутри: сохранить аудио (если `StoreAudio`) → распознать (`ISpeechTranscriber`) →
`TaskIntakeService.IntakeAsync(text, "voice", intentHint)` → запись в `actions` с длительностью.
Порога на уверенность распознавания нет: sherpa-onnx вероятность не отдаёт, да и решать
«на разбор или нет» должен классификатор интента — он отвечает за смысл фразы.
Если модель всё же отдаст вероятность, она сохранится в `meta.asrConfidence` и в журнале,
но на маршрут не повлияет.

Ограничения и коды: `413` — больше `MaxVoiceBytes` (5 МБ) или дольше `MaxVoiceSeconds` (30 с),
`415` — не WAV/PCM/16 кГц/моно, `422` — речь не распознана, `503` — распознавание не подключено.

Распознавание — `sherpa-onnx` (Whisper, офлайн, без сети). Пакет `org.k2fsa.sherpa.onnx`
подключается флагом, потому что тянет нативные библиотеки под каждую платформу:

```bash
./scripts/fetch-asr-model.ps1                       # whisper-small в models/asr
dotnet build -p:KoukiEnableSherpa=true              # сборка с распознаванием (restore + build одной командой)
Kouki__SpeechModelDirectory=models/asr/sherpa-onnx-whisper-small \
  dotnet Kagedama.api/bin/Debug/net10.0/Kagedama.api.dll --urls http://0.0.0.0:5199
```

Без флага собирается заглушка `UnavailableSpeechTranscriber`: сервер стартует и работает,
`/api/voice` отвечает `503` с текстом причины.

### Проверки речи без микрофона

`tools/Kouki.SpeechLab` синтезирует русскую фразу, прогоняет её через тот же распознаватель,
что стоит в сервере, и считает совпадение текста. Это проверка конвейера, а не замер качества:
датасет для обучения так не собирают.

```bash
./scripts/fetch-tts-model.ps1                                     # голос ru_RU-irina в models/tts
dotnet build tools/Kouki.SpeechLab -p:KoukiEnableSherpa=true

# синтез → распознавание → сравнение
dotnet tools/Kouki.SpeechLab/bin/Debug/net10.0/Kouki.SpeechLab.dll roundtrip "Запиши задачу купить хлеб"
#   Сказали: «Запиши задачу купить хлеб»
#   Услышали: «Запиши задачу купить хлеб.»
#   Совпадение: 100 %

# отдельно: WAV для /api/voice и распознавание готового файла
dotnet .../Kouki.SpeechLab.dll say  "Покажи мои задачи" --out phrase.wav
dotnet .../Kouki.SpeechLab.dll hear phrase.wav --lang ru
dotnet .../Kouki.SpeechLab.dll dump Tts      # фактические сигнатуры sherpa-onnx
```

Флаг нужно передавать и `restore`, и `build` — проще одной командой, как выше: обычный
`dotnet restore` без флага перезаписывает `project.assets.json`, и следующая сборка с флагом
падает на «нет типа `OfflineRecognizer`».

В архиве модели лежат обе версии графа (`small-encoder.onnx` и `small-encoder.int8.onnx`).
По умолчанию берётся **int8**: втрое меньше по памяти и заметно быстрее на процессоре,
а текст для короткой фразы тот же. Язык задаётся `Kouki__SpeechLanguage` (по умолчанию `ru`):
на русской речи так и надо оставлять; для проверки на чужом языке поставьте `en`, иначе
Whisper запишет английское в русской транслитерации.

Проверено на живом сервере с `whisper-small` (int8, один CPU-поток):

| Вход | Язык | Результат |
| --- | --- | --- |
| `test_wavs/0.wav` (6,6 с, английский) | `en` | `After early nightfall the yellow lamps would light up here and there the squalid quarter of the brothels.` — совпало с эталоном `trans.txt` |
| тот же файл | `ru` | русская транслитерация («После недели спорта, зелые лампы выживали здесь и там…») — ожидаемо для чужого языка |
| `test_wavs/8k.wav` (8 кГц) | — | `415` «Нужна частота 16000 Гц, а в файле 8000 Гц.» |
| мусорные байты | — | `503` от заглушки (без флага) |

Скорость: 6,6 с речи распознаются за 3,8–4,7 с, включая ленивую загрузку модели при первой фразе.

## Сборка и тесты

```bash
dotnet restore Kouki.slnx
dotnet build Kouki.slnx -c Release
dotnet test Kouki.Tests/Kouki.Tests.csproj
```

Требуется .NET 10 SDK (`global.json` разрешает любую feature-band 10.0). В окружениях без
доступа к nuget.org сборка возможна только из локального кеша пакетов; при многонодовом
MSBuild в песочнице используйте `-m:1 -nodeReuse:false` для `restore` и `build`.

`Kouki.slnx` собирается целиком: `Kagedama.vs` (расширение Visual Studio) из решения убрано —
оно таргетило `net8.0-windows8.0` и тянуло VS SDK, из-за чего решение не собиралось ни на
Linux, ни офлайн. Библиотеки и тесты собираются отдельно:

```bash
dotnet restore Kouki.Tests/Kouki.Tests.csproj
dotnet build Kouki.Tests/Kouki.Tests.csproj -c Release
dotnet test Kouki.Tests/Kouki.Tests.csproj --no-build -c Release
```

## Документация

- `docfx_project/docs/architecture.md` — архитектура и слои.
- `docfx_project/docs/dataflow.md` — путь команды от входной точки до хендлера.
- `docfx_project/docs/guides/development.md` — как добавить команду и прогнать тесты.
- `docfx_project/docs/guides/routers.md` — роутеры и декораторы.

## Лицензия

Файл LICENSE в репозитории отсутствует — добавьте его перед публикацией пакетов.

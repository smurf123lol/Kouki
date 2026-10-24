# AGENTS.md — правила работы с репозиторием Kouki

Адресовано человеку и агенту: здесь только то, что нельзя узнать из кода за минуту и что
ломается чаще всего при генерации изменений. Архитектура — в `docfx_project/docs/`,
состояние работ — в заметках воркспейса (`notes-updated\Задачи Kouki.md`).

## Что это за проект

Kouki — ядро голосового/текстового ассистента: разбирает фразу в интент, превращает его в
команду, хранит задачи и журнал разбора в SQLite. Наверху три хоста (API, MCP, Telegram-бот),
внизу — общая сборка DI. Прошивка станции живёт в отдельном репозитории `kouki-station`.

## Граф зависимостей (нарушать нельзя)

```
Kouki.Shimi            — контракты окружения (файлы, терминал, состояние, речь)
Kouki.Mugura           — команды: атрибуты, реестр, фабрика, диспетчер, роутеры
Kouki.Un               — распознавание речи (sherpa-onnx, включается флагом сборки)
Kouki.Ubusuna          — адаптеры окружения (консоль), конструктор контекста
Kouki.Yasabi           — понимание фразы: правила, журнал вердиктов, маршрут «интент → команда»
Kouki.Kagedama.Contracts — интерфейсы данных (без зависимостей вообще)
Kouki.Kagedama.Data    — SQLite: задачи, журнал, действия, состояние
Kouki.Kagedama         — прикладной слой: приём фразы, команды задач, аудит, единица работы
Kagedama.api / .mcp / Kouki.Kagedama.Bot — хосты
```

Правила: `Contracts` не ссылается ни на что; `Data` и `Yasabi` ссылаются на `Contracts`;
ядро (`Shimi`, `Mugura`, `Un`, `Ubusuna`) не знает про задачи; хосты не содержат бизнес-логики —
только транспорт и регистрацию.

## Граница ядра: Mugura — библиотека, а не приложение

`Kouki.Mugura` — переносимая механика команд. Её должны уметь взять в проект, где нет ни
задач, ни SQLite, ни журнала, ни нашего HTTP. Поэтому:

- **в `Kouki.Mugura` не появляются предметные типы**: задачи, журналы, интенты, «приём фраз».
  Механика (`[Command]`, `ICommandHandler<>`, `ICommandValidator<>`, `ICommandFactory`,
  `ICommandDispatcher`, `IRouter`/`StaticRouter`, `DelegatingRouter`) — да; «наша предметка» — нет.
- **команда создаётся в проекте-потребителе.** В этой сборке потребитель — `Kouki.Kagedama`
  (плюс библиотека `Kouki.Yasabi` для разбора фразы); в чужом продукте — его собственный проект.
  Ядро при этом не меняется вообще.
- **вариации транспорта — декораторы, а не правки ядра.** «Удалённый роутер», «роутер с
  логикой контекстов» (изначальные идеи `RemoteRouter`/`MushiRouter`) делаются наследником
  `DelegatingRouter` в проекте-потребителе: `StaticRouter` остаётся простым исполнителем.
- **проверка переносимости — ссылки.** У `Kouki.Mugura` в `.csproj` единственная
  `ProjectReference` — `Shimi`. Если там появился `Kouki.Kagedama.*`, `Kouki.Yasabi` или
  `Kagedama.*`, это дефект архитектуры, а не удобство.

Практическое следствие для агента: прежде чем класть тип в `Kouki.Mugura`, спроси себя —
«это понадобится проекту, у которого нет задач?» Если нет, тип живёт выше (в `Kouki.Kagedama`
или в самом продукте).

**Положение `Kouki.Ubusuna` — вопрос открытый.** Формально это «базовая надстройка» продукта
(консольные адаптеры + конструктор контекстов), логичнее заложить её в сам продукт, а часть
инструментов позже переносить из `Kouki.Kagedama` в `Ubusuna` по мере разбора. Решение отложено;
до него `Ubusuna` не обрастает прикладными понятиями (задачи, журнал, приём фразы) — только
адаптеры окружения. Задача — T43 в `notes-updated\Задачи Kouki.md`.

## Инварианты (если сомневаешься — так, и только так)

1. **Задачу создаёт только `Kouki.Kagedama/TaskFactory.cs`.** Ни в команде, ни в приёме, ни в
   эндпоинте не собирай `TaskRecord` руками: разъедется статус, приоритет и заголовок.
2. **Источник — только `TaskSources.*`** (`api`, `telegram`, `voice`, `station`, `dashboard`,
   `mcp`, `command`). Строку из запроса валидируй `TaskSources.IsKnown` и отвечай 400 со
   списком допустимых. Никаких `"api"` литералами.
3. **Статусы и приоритеты — только `TaskStatuses.*` / `TaskPriorities.*`.**
   «Открытая задача» — это `TaskStatuses.IsOpen(status)`, не `== "new"`.
4. **Вердикт классификатора нельзя перезаписывать.** Если фраза пришла с подсказкой сессии
   (`intent=add_task|ask`), в журнал пишется `hinted_intent`, а `intent/score/confident`
   остаются тем, что сказал классификатор. Это датасет для будущей модели — портить нельзя.
5. **Запись, которая трогает больше одной таблицы, идёт через `IUnitOfWork.RunAsync`.**
   Хранилища принимают `SqliteSession` и не открывают своих соединений: иначе задача, журнал
   и действие окажутся в разных транзакциях.
6. **Хосты собирают DI через `KoukiHosting.AddKoukiCore(new KoukiHostSettings { … })`** и
   инициализируются `KoukiHosting.InitializeKouki(provider, "api"|"mcp")`. Путь к базе по
   умолчанию — `KoukiHosting.DefaultDatabasePath()`, не свой.
7. **Чтение и запись по HTTP и MCP — через команды** (`list_tasks`, `patch_task`,
   `complete_task`, `list_actions`, `list_classifications`, `label_classification`),
   а не прямо в хранилища из хоста. Единственное исключение — `GET /api/tasks/{id}`:
   пиши рядом комментарий, почему это чтение одной записи мимо команды.
   Хост MCP поэтому получает в конструктор только `TaskIntakeService`, `IRouter` и
   `IContextRegistry`: если ему понадобилось хранилище, значит операция забыла команду.
8. **Авторизация fail-closed.** Пустой `Kouki:Token` — это ошибка запуска, а не «без пароля».
   Открытый режим — только явный `Kouki:AllowAnonymous=true`.
9. **Команды описываются атрибутом на классе-обработчике**, не на DTO:
   `[Command("name", Description=…, Category=…, Example=…)] public class Handler : ICommandHandler<Cmd>`.
   Опасное действие помечай `RequiresConfirmation = true`; хост обязан дать `ICommandConfirmation`.
10. **Фразу не режем.** Интент либо распознан целиком, либо фраза уходит в разбор как есть.
    Убирать «запиши задачу» из текста нельзя — это часть текста, который человек сказал.
11. **Никаких магических чисел.** Порог — `IntakeOptions.ConfidenceThreshold` (дефолт 0.75),
    лимиты — из `KoukiServerOptions`/`IntakeRequestPolicy`, приоритет — `TaskPriorities.Default`.
12. **Человеку — человеческий текст.** Внешние ошибки (Telegram, HTTP, SQL) не уходят
    в чат как `ex.Message`: хост разбирает код ответа и берёт текст из
    `IntakeReplyFormatter`/`Describe`; сам `ex.Message` остаётся в логах.
13. **Правила приёма живут в `Kouki.Kagedama/IntakeRequestPolicy.cs`.** Пустой текст,
    лимит длины, источник по умолчанию и закрытый список интентов (`SessionIntents`)
    проверяются там, а не в эндпоинте, MCP-инструменте или боте; порог для поля
    `confident` берётся из `TaskIntakeService.ConfidenceThreshold`, а не из `IOptions`.

## Где что менять

| Нужно | Куда идти |
| --- | --- |
| новая команда | в проект-потребитель: здесь `Kouki.Kagedama/Commands/*`, тест рядом (`Kouki.Tests`); в чужом продукте — его собственный проект, `Kouki.Mugura` не трогаем |
| новый тип декоратора роутера (удалённый, с контекстами) | наследник `DelegatingRouter` в проекте-потребителе, не правка `StaticRouter` |
| новый источник (телеграм, панель…) | `TaskSources` + валидация на границе |
| новый HTTP-эндпоинт | `Kagedama.api/Api/ApiEndpoints.cs` + DTO в `ApiContracts.cs` |
| новый MCP-инструмент | `Kagedama.mcp/KoukiMcpTools.cs` + дескриптор в `Tools` |
| правило классификации | `Kouki.Yasabi/RuleIntentClassifier.cs` (`DefaultRules`) |
| конфигурация | `KoukiServerOptions` (+ `appsettings.json`), секция `Kouki` одна на все хосты |
| схема БД | `Kouki.Kagedama.Data/DatabaseInitializer.cs` (схема идемпотентна) |

## Сборка и тесты

```powershell
dotnet restore Kouki.slnx -m:1 -nodeReuse:false
dotnet build   Kouki.slnx --no-restore -m:1 -nodeReuse:false     # 0 ошибок — норма
```

- **Офлайн-окружение:** `dotnet restore` без флагов может падать молча («Ошибок: 0», код 1) —
  это многонодовый MSBuild, которому запрещены named pipes. Всегда `-m:1 -nodeReuse:false`.
- **Тесты:** `dotnet test` в песочнице недоступен (testhost падает на `OpenProcess`). Раннер —
  `E:\dsharness-workspaces\Kouki Workspace\_scratch\testrun`, запуск
  `dotnet _scratch\testrun\bin\Debug\net10.0\Kouki.Tests.Runner.dll`. Он исполняет только
  `[Fact]`; `[Theory]` **не запускается** — пиши `[Fact]` с циклом внутри.
- **После переноса сборок** (`AssemblyName`, новые проекты) чисти `_scratch\testrun\bin` и
  `obj`: иначе раннер падает `FileNotFoundException` на старой копии.
- **Распознавание речи** включается флагом и требует его и на restore, и на build:
  `dotnet restore Kagedama.api\Kagedama.api.csproj -p:KoukiEnableSherpa=true -m:1 -nodeReuse:false`,
  затем `dotnet build … --no-restore -p:KoukiEnableSherpa=true -m:1 -nodeReuse:false`.
  Готовая команда — `tools\run-server.ps1` (собирает с флагом и поднимает сервер).
- **Модели** (`models\asr`, `models\tts`) не коммитятся, качаются
  `scripts\fetch-asr-model.ps1` и `scripts\fetch-tts-model.ps1`.

## Гигиена файлов

- Временные файлы, логи, БД-стенды — только в `E:\dsharness-workspaces\Kouki Workspace\_scratch`.
  В корне воркспейса и в корне репозитория ничего временного не оставлять.
- `bin/`, `obj/`, `models/`, `*.sqlite`, `station-audio/` — в `.gitignore`; проверить перед
  коммитом, что в индекс не попали.
- `docfx_project/_site` и `docfx_project/api` — сгенерированные, в индекс не возвращать.

## Что обновлять вместе с кодом

Изменение поведения — это не только код:

1. `notes-updated\Архитектурная заметка.md` и `notes-updated\датафлоу и типизация.md`
   (воркспейс) — как устроено сейчас;
2. `notes-updated\Задачи Kouki.md` — статус задачи (✅/🟡/⛔) и дата;
3. `README.md` — разделы, которые читает новый человек;
4. `docfx_project/docs/architecture.md` и `dataflow.md` — если менялись контракты.

## Чего делать нельзя

- Менять `Kouki.Kagedama.Contracts` ради удобства хоста: от этого зависят все.
- Писать в журнал «правильный» интент вместо классификатора (см. инвариант 4).
- Открывать `SqliteConnection` вне `SqliteSession`.
- Логировать токены, пароли и тела с персональными данными.
- Оставлять `[Theory]`, «временно» отключённые тесты и `catch { }` без лога.

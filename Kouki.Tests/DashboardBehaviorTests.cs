using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Xunit;
using Xunit.Sdk;

namespace Kouki.Tests;

/// <summary>
/// Поведение дашборда: <c>app.js</c> исполняется в node с поддельным DOM, а тест смотрит
/// на получившуюся разметку, на состояние фильтра и на запросы к API, а не на подстроки
/// в исходнике.
/// </summary>
/// <remarks>
/// Почему node: дашборд — обычный браузерный скрипт без сборки, и «тест поведения»
/// можно сделать только исполнив его. Грепать <c>task.tags</c> недостаточно: если убрать
/// <c>${tagChips(task.tags)}</c> из шаблона карточки, подстрока <c>task.tags</c> останется
/// (она ещё встречается в ключе перерисовки), а чипы исчезнут. Node есть там, где гоняются
/// тесты (Linux-сервер и Windows-машина с DSH); если его нет, тест падает с понятным текстом,
/// а не тихо пропускается.
/// </remarks>
public sealed class DashboardBehaviorTests
{
    [Fact]
    public void Board_has_five_columns_top_level_cards_and_tag_filter()
    {
        JsonNode result = RunDashboard();

        JsonArray columns = result["board"]!["columns"]!.AsArray();
        Assert.Equal(5, columns.Count);
        Assert.Equal(
            new[] { "new", "in_progress", "needs_review", "done", "cancelled" },
            columns.Select((node) => node!["status"]!.GetValue<string>()).ToArray());
        Assert.Equal("3", columns[0]!["count"]!.GetValue<string>());

        // «Отменено» свёрнута при загрузке, тело скрыто классом.
        Assert.True(columns[4]!["collapsed"]!.GetValue<bool>());

        // Доска просит только корневые задачи и не фильтрует по статусу: колонки — на клиенте.
        string request = result["board"]!["request"]!.GetValue<string>();
        Assert.Contains("topLevel=true", request, StringComparison.Ordinal);
        Assert.Contains("limit=500", request, StringComparison.Ordinal);
        Assert.DoesNotContain("status=", request, StringComparison.Ordinal);

        JsonArray cards = result["board"]!["cards"]!.AsArray();
        Assert.Equal(3, cards.Count);

        string taggedCard = cards[0]!.GetValue<string>();
        string plainCard = cards[1]!.GetValue<string>();
        Assert.Contains("data-tag=\"работа\"", taggedCard, StringComparison.Ordinal);
        Assert.Contains(">#работа<", taggedCard, StringComparison.Ordinal);
        Assert.DoesNotContain("data-tag", plainCard, StringComparison.Ordinal);

        // Карточка: заголовок, бейдж приоритета, счётчик подзадач.
        Assert.Contains("ответить на письмо", taggedCard, StringComparison.Ordinal);
        Assert.Contains("class=\"priority priority-2\"", taggedCard, StringComparison.Ordinal);
        Assert.Contains("Подзадачи: 2 · готово 1", taggedCard, StringComparison.Ordinal);

        // Клик по чипу ставит фильтр и не открывает задачу (обработчик забирает событие).
        JsonNode firstClick = result["firstClick"]!;
        Assert.Equal("работа", firstClick["tag"]!.GetValue<string>());
        Assert.Equal(1, firstClick["prevented"]!.GetValue<int>());
        Assert.Contains(
            "tag=" + Uri.EscapeDataString("работа"),
            firstClick["url"]!.GetValue<string>(),
            StringComparison.Ordinal);

        // Повторный клик по тому же чипу снимает фильтр.
        JsonNode secondClick = result["secondClick"]!;
        Assert.Equal(string.Empty, secondClick["tag"]!.GetValue<string>());
        Assert.DoesNotContain("tag=", secondClick["url"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Board_card_expands_direct_subtasks_and_creates_them()
    {
        JsonNode result = RunDashboard();
        JsonNode subtasks = result["subtasks"]!;

        // Чек-лист раскрывается кликом и показывает прямых детей: готовый — с галочкой
        // и зачёркнутый, открытый — без галочки.
        string openHtml = subtasks["openHtml"]!.GetValue<string>();
        Assert.Contains("Подзадачи: 2 · готово 1", openHtml, StringComparison.Ordinal);
        Assert.Contains("data-subtask-toggle=\"aaaaaaaa-aaaa-aaaa-aaaa-000000000001\"", openHtml, StringComparison.Ordinal);
        Assert.Contains("checked", openHtml, StringComparison.Ordinal);
        Assert.Contains("is-closed", openHtml, StringComparison.Ordinal);
        Assert.Contains("купить молоко", openHtml, StringComparison.Ordinal);
        Assert.Contains("позвонить в банк", openHtml, StringComparison.Ordinal);
        Assert.Contains("Добавить подзадачу", openHtml, StringComparison.Ordinal);

        // У задачи без детей пустой чек-лист говорит «Подзадач нет».
        string emptyHtml = subtasks["emptyHtml"]!.GetValue<string>();
        Assert.Contains("Подзадач нет", emptyHtml, StringComparison.Ordinal);

        // Пустой ввод — сообщение, а не запрос на сервер.
        string emptyErrorHtml = subtasks["emptyErrorHtml"]!.GetValue<string>();
        Assert.Contains("Введите текст подзадачи.", emptyErrorHtml, StringComparison.Ordinal);
        Assert.Empty(subtasks["createRequests"]!.AsArray());

        // Непустой ввод создаёт подзадачу с parentId и перерисовывает счётчик.
        JsonArray createRequests = subtasks["createdRequests"]!.AsArray();
        Assert.Single(createRequests);
        Assert.Equal("купить хлеб", createRequests[0]!["text"]!.GetValue<string>());
        Assert.Equal(
            "11111111-1111-1111-1111-111111111111",
            createRequests[0]!["parentId"]!.GetValue<string>());

        string afterAddHtml = subtasks["afterAddHtml"]!.GetValue<string>();
        Assert.Contains("купить хлеб", afterAddHtml, StringComparison.Ordinal);
        Assert.Contains("Подзадачи: 3 · готово 1", afterAddHtml, StringComparison.Ordinal);

        // Галочка переводит подзадачу в done; счётчик «готово» растёт.
        JsonArray patches = subtasks["patchRequests"]!.AsArray();
        Assert.Single(patches);
        Assert.Equal("done", patches[0]!["body"]!["status"]!.GetValue<string>());
        string afterToggleHtml = subtasks["afterToggleHtml"]!.GetValue<string>();
        Assert.Contains("Подзадачи: 3 · готово 2", afterToggleHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Board_moves_cards_between_columns_and_restores_on_failure()
    {
        JsonNode result = RunDashboard();
        JsonNode move = result["move"]!;

        Assert.True(move["dragStarted"]!.GetValue<bool>());

        JsonArray requests = move["requests"]!.AsArray();
        Assert.Equal(2, requests.Count);
        Assert.Equal(
            "in_progress",
            requests[0]!["body"]!["status"]!.GetValue<string>());
        Assert.Equal(0, requests[0]!["body"]!["position"]!.GetValue<int>());

        // После успеха карточка стоит в новой колонке, а не осталась в старой.
        Assert.Contains(
            "11111111-1111-1111-1111-111111111111",
            move["inProgressHtml"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "11111111-1111-1111-1111-111111111111",
            move["newHtml"]!.GetValue<string>(),
            StringComparison.Ordinal);

        // Сигнатура карточки учитывает статус и позицию: иначе applyList не переставит её.
        string signature = move["signature"]!.GetValue<string>();
        Assert.Contains("in_progress", signature, StringComparison.Ordinal);
        Assert.Contains("|0|", signature, StringComparison.Ordinal);

        // Ошибка сети: карточка возвращается на место и видит человеческий текст.
        Assert.Contains("Не удалось переместить задачу.", move["banner"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "22222222-2222-2222-2222-222222222222",
            move["doneHtml"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Board_card_buttons_change_status_via_patch()
    {
        JsonNode result = RunDashboard();
        JsonNode status = result["status"]!;

        // Кнопка статуса на карточке — PATCH, а не move.
        JsonArray requests = status["requests"]!.AsArray();
        Assert.Single(requests);
        Assert.Equal("55555555-5555-5555-5555-555555555555", requests[0]!["id"]!.GetValue<string>());
        Assert.Equal("done", requests[0]!["body"]!["status"]!.GetValue<string>());
        Assert.Equal(2, status["moves"]!.GetValue<int>());

        // Карточка переехала в «Готово» после перезагрузки списка.
        Assert.Contains(
            "55555555-5555-5555-5555-555555555555",
            status["doneHtml"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "55555555-5555-5555-5555-555555555555",
            status["newHtml"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Board_deletes_cascade_and_detach_with_confirmations()
    {
        JsonNode result = RunDashboard();
        JsonNode deletes = result["deletes"]!;

        // Каскад: текст подтверждения и обязательный confirm=true.
        Assert.Equal(
            "Удалить задачу вместе со всеми подзадачами? Подзадач: 1.",
            deletes["cascade"]!["confirm"]!.GetValue<string>());
        Assert.Contains(
            "mode=cascade&confirm=true",
            deletes["cascade"]!["url"]!.GetValue<string>(),
            StringComparison.Ordinal);

        // Detach: другой текст, подтверждение серверу не нужно.
        Assert.Equal(
            "Удалить задачу? Подзадачи станут самостоятельными. Подзадач: 0.",
            deletes["detach"]!["confirm"]!.GetValue<string>());
        string detachUrl = deletes["detach"]!["url"]!.GetValue<string>();
        Assert.Contains("mode=detach", detachUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("confirm=true", detachUrl, StringComparison.Ordinal);

        // Обе карточки ушли с доски, осталась только перенесённая.
        Assert.Equal(1, deletes["remaining"]!.GetValue<int>());
    }

    [Fact]
    public void Board_autorefresh_skips_a_tick_while_dragging_or_editing()
    {
        JsonNode tick = RunDashboard()["tick"]!;

        Assert.Equal(0, tick["dragging"]!.GetValue<int>());
        Assert.Equal(0, tick["editing"]!.GetValue<int>());
        Assert.True(tick["normal"]!.GetValue<int>() > 0);
    }

    [Fact]
    public void Board_is_one_column_with_tabs_on_mobile_and_drag_is_off()
    {
        JsonNode result = RunDashboard();
        JsonNode mobile = result["mobile"]!;

        // На мобильной ширине dragstart отменяется и перемещения не происходит.
        Assert.Equal(1, mobile["prevented"]!.GetValue<int>());
        Assert.False(mobile["dragging"]!.GetValue<bool>());
        Assert.Equal(2, mobile["moveRequests"]!.GetValue<int>());

        // Вкладки показывают одну колонку: активной стала выбранная.
        Assert.True(mobile["doneActive"]!.GetValue<bool>());
        Assert.False(mobile["newActive"]!.GetValue<bool>());

        // «Отменено» разворачивается и снова сворачивается, текст кнопки меняется.
        JsonNode collapsed = result["cancelled"]!;
        Assert.Equal("Свернуть", collapsed["expandedText"]!.GetValue<string>());
        Assert.False(collapsed["expanded"]!.GetValue<bool>());
        Assert.Equal("Показать отменённые", collapsed["collapsedText"]!.GetValue<string>());
        Assert.True(collapsed["collapsed"]!.GetValue<bool>());
    }

    [Fact]
    public void Task_card_shows_recognition_history_and_loads_audio_on_demand()
    {
        JsonNode result = RunDashboard();
        JsonNode card = result["taskCard"]
            ?? throw new XunitException("харнесс не вернул карточку задачи");

        // Карточка фраз — отдельный запрос с фильтром по задаче.
        string classifications = string.Join(
            "\n",
            card["classificationRequests"]!.AsArray().Select((node) => node!.GetValue<string>()));
        Assert.Contains(
            "/api/classifications?taskId=11111111-1111-1111-1111-111111111111&limit=50",
            classifications,
            StringComparison.Ordinal);

        // Порядок блоков: «Действия» → «История распознавания» → «Разбор (meta)».
        string html = card["detailHtml"]!.GetValue<string>();
        int actionsAt = html.IndexOf("Действия", StringComparison.Ordinal);
        int phrasesAt = html.IndexOf("История распознавания", StringComparison.Ordinal);
        int metaAt = html.IndexOf("Разбор (meta)", StringComparison.Ordinal);
        Assert.True(
            actionsAt >= 0 && phrasesAt > actionsAt && metaAt > phrasesAt,
            "блок «История распознавания» должен стоять между «Действия» и «Разбор (meta)»: " + html);

        // Запись: текст фразы, интент, модель, время, процент уверенности.
        Assert.Contains("«купи молоко и хлеб»", html, StringComparison.Ordinal);
        Assert.Contains("add_task", html, StringComparison.Ordinal);
        Assert.Contains("hybrid", html, StringComparison.Ordinal);
        Assert.Contains("98%", html, StringComparison.Ordinal);
        Assert.Contains("41%", html, StringComparison.Ordinal);
        // confident = false помечается, а не роняет рендер.
        Assert.Contains("неуверенно", html, StringComparison.Ordinal);
        Assert.Matches(@"\d{2}:\d{2}", html);

        // Аудио: элемент на месте, но файл не качается до действия пользователя.
        Assert.Equal(0, card["audioRequestsBeforeClick"]!.GetValue<int>());
        Assert.Contains("data-audio-play=\"33333333-3333-3333-3333-333333333333\"", html, StringComparison.Ordinal);
        Assert.Contains("<audio controls preload=\"none\"", html, StringComparison.Ordinal);

        JsonArray audioCalls = card["audioCalls"]!.AsArray();
        Assert.Single(audioCalls);
        Assert.EndsWith(
            "/api/audio/33333333-3333-3333-3333-333333333333",
            audioCalls[0]!["url"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Equal("Bearer test-token", audioCalls[0]!["authorization"]!.GetValue<string>());

        JsonArray createdUrls = card["createdUrls"]!.AsArray();
        Assert.Single(createdUrls);
        Assert.Empty(card["revokedBeforeReopen"]!.AsArray());

        // Повторное открытие с пустым списком фраз: сообщение и отзыв blob-URL.
        string emptyHtml = card["emptyHtml"]!.GetValue<string>();
        Assert.Contains("Связанных фраз нет.", emptyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("data-audio-play", emptyHtml, StringComparison.Ordinal);

        string createdUrl = createdUrls[0]!.GetValue<string>();
        JsonArray revoked = card["revoked"]!.AsArray();
        Assert.Contains(revoked, (node) => node!.GetValue<string>() == createdUrl);
    }

    [Fact]
    public void Board_escapes_html_in_title_and_tags()
    {
        JsonNode result = RunDashboard();
        string html = result["escaping"]!["html"]!.GetValue<string>();

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(2)&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
    }

    /// <summary>Исполняет харнесс в node и разбирает его JSON.</summary>
    private static JsonNode RunDashboard()
    {
        string appJs = Path.Combine(TestPaths.RepoRoot(), "Kagedama.api", "wwwroot", "app.js");
        Assert.True(File.Exists(appJs), $"нет файла дашборда: {appJs}");

        string directory = Path.Combine(Path.GetTempPath(), "kouki-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "dashboard-behavior.test.js");

        try
        {
            File.WriteAllText(script, Harness);

            (int exitCode, string stdout, string stderr) = RunNode(script, appJs);
            Assert.True(
                exitCode == 0,
                $"node завершился с кодом {exitCode}:{Environment.NewLine}{stderr}");

            return JsonNode.Parse(stdout)
                ?? throw new XunitException("node вернул не JSON: " + stdout);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Временный каталог за собой уберёт система: тест уже не от этого зависит.
            }
        }
    }

    /// <summary>
    /// Запускает харнесс в node: путь к скрипту и путь к <c>app.js</c> — аргументами.
    /// </summary>
    private static (int ExitCode, string Stdout, string Stderr) RunNode(string scriptPath, string appJsPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(appJsPath);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception error)
        {
            throw new XunitException(
                "Для проверки поведения дашборда нужен node в PATH (он есть на сервере и на " +
                "Windows-машине с DSH). Если node нет — тест не пропускается молча: " +
                error.Message);
        }

        using (process)
        {
            if (process is null)
            {
                throw new XunitException("node не запустился.");
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                throw new XunitException("node не закончил за минуту: харнесс дашборда завис.");
            }

            return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }
    }

    /// <summary>
    /// Харнесс: поддельный DOM и песочница, в которой исполняется настоящий <c>app.js</c>.
    /// </summary>
    /// <remarks>
    /// Минимум, который нужен скрипту: элементы с <c>dataset</c>/<c>children</c>/<c>classList</c>,
    /// <c>document.createElement('template')</c> с <c>content.firstElementChild</c> (так работает
    /// <c>applyList</c>), запоминание глобальных обработчиков и <c>fetch</c>, который отвечает
    /// как сервер. Сценарии идут по порядку: доска → теги → карточка → подзадачи → move →
    /// удаление → мобильный режим; результат печатается одним JSON.
    /// </remarks>
    private const string Harness = """
'use strict';
// Дашборд в песочнице: app.js исполняется целиком, но вместо браузера — поддельный DOM.
// Скрипт печатает один JSON; утверждения делает C#-тест.
const fs = require('fs');

const appPath = process.argv[2];
const source = fs.readFileSync(appPath, 'utf8');

const AUDIO_ID = '33333333-3333-3333-3333-333333333333';
const T1 = '11111111-1111-1111-1111-111111111111';
const T2 = '22222222-2222-2222-2222-222222222222';
const T3 = '55555555-5555-5555-5555-555555555555';
const CHILD_DONE = 'aaaaaaaa-aaaa-aaaa-aaaa-000000000001';
const CHILD_OPEN = 'aaaaaaaa-aaaa-aaaa-aaaa-000000000002';
const CHILD_NEW = 'aaaaaaaa-aaaa-aaaa-aaaa-000000000003';

const BOARD_STATUSES = ['new', 'in_progress', 'needs_review', 'done', 'cancelled'];

const CLASSIFICATIONS = [
    {
        id: AUDIO_ID,
        createdAt: new Date().toISOString(),
        source: 'station',
        inputKind: 'asr',
        inputText: 'купи молоко и хлеб',
        intent: 'add_task',
        score: 0.98,
        confident: true,
        model: 'hybrid',
        error: null,
        label: null,
        labeledAt: null,
        taskId: T1,
        hasAudio: true,
    },
    {
        id: '44444444-4444-4444-4444-444444444444',
        createdAt: new Date(Date.now() - 3600000).toISOString(),
        source: 'telegram',
        inputKind: 'text',
        inputText: 'напомни про встречу',
        intent: 'add_task',
        score: 0.41,
        confident: false,
        model: 'rules',
        error: null,
        label: null,
        labeledAt: null,
        taskId: T1,
        hasAudio: false,
    },
];

// Сценарий теста переключает выдачу: после проигрывания аудио фразы «исчезают».
let classifications = CLASSIFICATIONS;

function rootTask(id, title, status, position, tags, subtasksCount, subtasksDoneCount) {
    return {
        id: id,
        title: title,
        status: status,
        priority: 2,
        parentId: null,
        treeId: id,
        position: position,
        subtasksCount: subtasksCount,
        subtasksDoneCount: subtasksDoneCount,
        tags: tags,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        rawText: title,
        source: 'api',
        meta: null,
    };
}

let rootTasks = [
    rootTask(T1, 'ответить на письмо', 'new', 0, ['работа'], 2, 1),
    rootTask(T2, 'позвонить маме', 'new', 1, [], 0, 0),
    rootTask(T3, 'старая задача', 'new', 2, [], 1, 0),
];

const SUBTASKS = new Map([
    [T1, [
        {
            id: CHILD_DONE, title: 'купить молоко', status: 'done', priority: 2,
            parentId: T1, position: 0, createdAt: new Date().toISOString(),
        },
        {
            id: CHILD_OPEN, title: 'позвонить в банк', status: 'new', priority: 2,
            parentId: T1, position: 1, createdAt: new Date().toISOString(),
        },
    ]],
    [T3, [
        {
            id: CHILD_NEW, title: 'выбросить коробки', status: 'new', priority: 2,
            parentId: T3, position: 0, createdAt: new Date().toISOString(),
        },
    ]],
]);

function taskById(id) {
    return rootTasks.find((task) => task.id === id);
}

function syncCounts(parentId) {
    const items = SUBTASKS.get(parentId) || [];
    const parent = taskById(parentId);
    if (!parent) return;
    parent.subtasksCount = items.length;
    parent.subtasksDoneCount = items.filter((item) => item.status === 'done').length;
}

function makeClassList() {
    const classes = new Set();
    return {
        add(name) { classes.add(name); },
        remove(name) { classes.delete(name); },
        contains(name) { return classes.has(name); },
        toggle(name, force) {
            const next = force === undefined ? !classes.has(name) : Boolean(force);
            if (next) classes.add(name); else classes.delete(name);
            return next;
        },
    };
}

function makeElement(id) {
    const element = {
        id: id || null,
        dataset: {},
        hidden: false,
        textContent: '',
        value: '',
        files: null,
        children: [],
        parent: null,
        _html: '',
        listeners: {},
        style: {},
        classList: makeClassList(),
        addEventListener(type, handler) { this.listeners[type] = handler; },
        append(node) { node.parent = this; this.children.push(node); },
        replaceChildren() {
            this.children = Array.prototype.slice.call(arguments);
            this.children.forEach((node) => { node.parent = this; });
        },
        remove() {
            if (!this.parent) return;
            const index = this.parent.children.indexOf(this);
            if (index >= 0) this.parent.children.splice(index, 1);
        },
        replaceWith(node) {
            if (!this.parent) return;
            const index = this.parent.children.indexOf(this);
            if (index >= 0) this.parent.children[index] = node;
            node.parent = this.parent;
        },
        // applyList ищет существующие узлы по data-list-key: после первой отрисовки
        // это ровно его же дети.
        querySelectorAll() { return this.children.slice(); },
        querySelector() { return null; },
        requestSubmit() { if (this.listeners.submit) this.listeners.submit({ preventDefault() {} }); },
        get firstElementChild() { return this.children.length ? this.children[0] : null; },
        set innerHTML(html) {
            this._html = String(html);
            const child = makeElement(null);
            child._html = this._html;
            child.parent = this;
            this.children = this._html ? [child] : [];
        },
        get innerHTML() { return this._html; },
    };

    // Шаблон в браузере хранит разметку в content, а не в детях.
    Object.defineProperty(element, 'content', {
        get() {
            return { firstElementChild: element.children.length ? element.children[0] : null };
        },
    });

    return element;
}

const elements = new Map();

function elementById(id) {
    if (!elements.has(id)) elements.set(id, makeElement(id));
    return elements.get(id);
}

const documentListeners = {};

const document = {
    hidden: false,
    activeElement: null,
    getElementById: elementById,
    createElement() { return makeElement(null); },
    querySelectorAll() { return []; },
    querySelector() { return null; },
    addEventListener(type, handler) { documentListeners[type] = handler; },
};

const localStorage = {
    getItem(key) { return key === 'kouki.token' ? 'test-token' : ''; },
    setItem() {},
};

let desktop = true;
let confirmMessage = '';
const sandboxWindow = {
    matchMedia(query) {
        return { media: query, matches: desktop };
    },
    confirm(message) {
        confirmMessage = message;
        return true;
    },
};

const requests = [];
const calls = [];
const urlsCreated = [];
const urlsRevoked = [];
const moveRequests = [];
const deleteRequests = [];
const patchRequests = [];
const createRequests = [];
let failMoves = false;

const sandboxURL = {
    createObjectURL(blob) {
        const url = 'blob:mock-' + (urlsCreated.length + 1);
        urlsCreated.push({ url: url, size: blob && blob.size });
        return url;
    },
    revokeObjectURL(url) { urlsRevoked.push(url); },
};

function jsonResponse(payload) {
    return {
        status: 200,
        ok: true,
        json: async function () { return payload; },
        text: async function () { return JSON.stringify(payload); },
    };
}

function errorResponse(status, payload) {
    return {
        status: status,
        ok: false,
        json: async function () { return payload; },
        text: async function () { return JSON.stringify(payload); },
    };
}

function blobResponse() {
    return {
        status: 200,
        ok: true,
        blob: async function () { return { size: 3 }; },
        text: async function () { return ''; },
    };
}

async function fakeFetch(url, options) {
    const href = String(url);
    const opts = options || {};
    const method = String(opts.method || 'GET').toUpperCase();
    requests.push(href);
    calls.push({ url: href, options: opts, method: method });

    if (href.indexOf('/api/audio/') >= 0) return blobResponse();
    if (href.indexOf('/api/classifications') >= 0) return jsonResponse(classifications);
    if (href.indexOf('/api/actions') >= 0) return jsonResponse([]);
    if (href.indexOf('/api/telemetry') >= 0) {
        return jsonResponse({
            windowHours: 24,
            since: new Date().toISOString(),
            totals: { requests: 42, errors: 3, avgDurationMs: 120.5, audioBytes: 64000 },
            byEndpoint: [{ endpoint: 'voice', requests: 30, errors: 2, avgDurationMs: 150.0, audioBytes: 64000 }],
            bySource: [{ source: 'station', requests: 30, errors: 2 }],
        });
    }

    const move = href.match(/\/api\/tasks\/([0-9a-f-]+)\/move$/);
    if (move && method === 'POST') {
        const body = JSON.parse(opts.body);
        moveRequests.push({ id: move[1], body: body });
        if (failMoves) return errorResponse(500, { error: 'сеть недоступна' });
        const task = taskById(move[1]);
        if (task) {
            task.status = body.status;
            task.position = body.position;
        }
        return jsonResponse(task);
    }

    if (method === 'DELETE') {
        const id = href.match(/\/api\/tasks\/([0-9a-f-]+)/)[1];
        deleteRequests.push({ url: href, confirm: confirmMessage });
        const index = rootTasks.findIndex((task) => task.id === id);
        if (index >= 0) rootTasks.splice(index, 1);
        SUBTASKS.delete(id);
        return jsonResponse({
            taskId: id,
            deleted: true,
            mode: href.indexOf('detach') >= 0 ? 'detach' : 'cascade',
            removedSubtasks: 0,
        });
    }

    if (method === 'PATCH') {
        const id = href.match(/\/api\/tasks\/([0-9a-f-]+)/)[1];
        const body = JSON.parse(opts.body);
        patchRequests.push({ id: id, body: body });
        for (const [parentId, items] of SUBTASKS) {
            const child = items.find((item) => item.id === id);
            if (child) {
                child.status = body.status;
                syncCounts(parentId);
                return jsonResponse(child);
            }
        }
        // Статус корневой задачи меняется тем же PATCH, что и у подзадачи.
        const root = taskById(id);
        if (root) root.status = body.status;
        return jsonResponse(root);
    }

    if (method === 'POST' && href === '/api/tasks') {
        const body = JSON.parse(opts.body);
        createRequests.push(body);
        const child = {
            id: CHILD_NEW,
            title: body.text,
            status: 'new',
            priority: body.priority || 2,
            parentId: body.parentId,
            position: 0,
            createdAt: new Date().toISOString(),
        };
        const list = SUBTASKS.get(body.parentId) || [];
        list.push(child);
        SUBTASKS.set(body.parentId, list);
        syncCounts(body.parentId);
        return jsonResponse(child);
    }

    if (href.indexOf('parentId=') >= 0) {
        const parentId = decodeURIComponent(href.split('parentId=')[1].split('&')[0]);
        return jsonResponse(SUBTASKS.get(parentId) || []);
    }

    if (/\/api\/tasks\/[0-9a-f-]+$/.test(href) && method === 'GET') {
        return jsonResponse(taskById(href.split('/').pop()));
    }

    if (href.indexOf('/api/tasks') >= 0) return jsonResponse(rootTasks);
    if (href.indexOf('/api/health') >= 0) return jsonResponse({ utc: new Date().toISOString() });
    return jsonResponse([]);
}

const sandboxConsole = { info() {}, log() {}, warn() {}, error() {} };
let refreshTick = null;
const sandboxSetInterval = function (fn) {
    refreshTick = fn;
    return 0;
};

const app = new Function(
    'document',
    'localStorage',
    'fetch',
    'setInterval',
    'console',
    'URL',
    'window',
    source + '\nreturn { state: state };'
)(document, localStorage, fakeFetch, sandboxSetInterval, sandboxConsole, sandboxURL, sandboxWindow);

const wait = () => new Promise((resolve) => setTimeout(resolve, 0));

async function waitFor(condition, label) {
    for (let i = 0; i < 400 && !condition(); i += 1) await wait();
    if (!condition()) throw new Error('не дождались: ' + label);
}

function boardBody(status) {
    return elementById('board-body-' + status);
}

function cardHtml(status, id) {
    return boardBody(status).children.map((node) => node.innerHTML)
        .find((html) => html.indexOf('data-board-card="' + id + '"') >= 0) || '';
}

function targetEvent(map, extra) {
    const event = Object.assign({
        target: {
            closest(selector) { return map[selector] || null; },
        },
        preventDefault() { event.prevented = (event.prevented || 0) + 1; },
        stopPropagation() {},
        prevented: 0,
    }, extra || {});
    return event;
}

function tasksUrl() {
    const all = requests.filter((url) => url.indexOf('/api/tasks?') >= 0
        && url.indexOf('parentId=') < 0);
    return all.length ? all[all.length - 1] : '';
}

(async () => {
    // ---- доска: пять колонок, только корневые задачи ----
    await waitFor(() => boardBody('new').children.length === 3, 'три карточки на доске');

    const board = {
        columns: BOARD_STATUSES.map((status) => ({
            status: status,
            count: elementById('board-count-' + status).textContent,
            collapsed: elementById('board-column-' + status).classList.contains('board-column-collapsed'),
        })),
        cards: boardBody('new').children.map((node) => node.innerHTML),
        request: tasksUrl(),
    };

    // ---- фильтр по тегу ----
    const clickHandler = documentListeners.click;
    if (typeof clickHandler !== 'function') throw new Error('нет глобального обработчика клика');

    const chip = { dataset: { tag: 'работа' } };
    const tagEvent = () => targetEvent({ '[data-tag]': chip });

    let event = tagEvent();
    await clickHandler(event);
    await wait();
    const firstClick = { tag: app.state.tag, url: tasksUrl(), prevented: event.prevented };

    event = tagEvent();
    await clickHandler(event);
    await wait();
    const secondClick = { tag: app.state.tag, url: tasksUrl() };

    // ---- карточка задачи: история распознавания и ленивая загрузка аудио ----
    const detailBody = elementById('detail-body');
    const taskEvent = () => targetEvent({ '[data-task]': { dataset: { task: T1 } } });

    await clickHandler(taskEvent());
    await waitFor(() => detailBody.innerHTML.indexOf('История распознавания') >= 0, 'история распознавания');

    const detailHtml = detailBody.innerHTML;
    const classificationRequests = requests.filter((url) => url.indexOf('/api/classifications?taskId=') >= 0);
    const audioRequestsBeforeClick = requests.filter((url) => url.indexOf('/api/audio/') >= 0).length;
    const revokedBeforeReopen = urlsRevoked.slice();

    await clickHandler(targetEvent({ '[data-audio-play]': { dataset: { audioPlay: AUDIO_ID } } }));
    await waitFor(() => urlsCreated.length > 0, 'blob-URL аудио');

    const audioCalls = calls
        .filter((call) => call.url.indexOf('/api/audio/') >= 0)
        .map((call) => ({
            url: call.url,
            authorization: (call.options && call.options.headers && call.options.headers.Authorization) || '',
        }));

    classifications = [];
    await clickHandler(taskEvent());
    await waitFor(() => detailBody.innerHTML.indexOf('Связанных фраз нет.') >= 0, 'пустые фразы');

    const taskCard = {
        detailHtml: detailHtml,
        emptyHtml: detailBody.innerHTML,
        classificationRequests: classificationRequests,
        audioRequestsBeforeClick: audioRequestsBeforeClick,
        audioCalls: audioCalls,
        createdUrls: urlsCreated.map((entry) => entry.url),
        revokedBeforeReopen: revokedBeforeReopen,
        revoked: urlsRevoked,
    };

    // ---- подзадачи: раскрытие, пустой чек-лист, пустой ввод, создание, галочка ----
    const subtaskEvent = (id) => targetEvent({ '[data-subtasks-open]': { dataset: { subtasksOpen: id } } });

    await clickHandler(subtaskEvent(T1));
    const openHtml = cardHtml('new', T1);

    await clickHandler(subtaskEvent(T2));
    const emptyHtml = cardHtml('new', T2);

    const addEvent = (id) => targetEvent({ '[data-subtask-add]': { dataset: { subtaskAdd: id } } });
    await clickHandler(addEvent(T2));
    const emptyErrorHtml = cardHtml('new', T2);
    const createBefore = createRequests.length;

    documentListeners.input({
        target: {
            closest(selector) {
                return selector === '[data-subtask-input]'
                    ? { dataset: { subtaskInput: T1 }, value: 'купить хлеб' }
                    : null;
            },
        },
    });
    await clickHandler(addEvent(T1));
    await waitFor(() => createRequests.length > createBefore, 'запрос на создание подзадачи');
    await wait();

    const afterAddHtml = cardHtml('new', T1);
    const newChildId = CHILD_NEW;

    await clickHandler(targetEvent({ '[data-subtask-toggle]': { dataset: { subtaskToggle: newChildId } } }));
    await waitFor(() => patchRequests.length > 0, 'PATCH подзадачи');
    await wait();
    const afterToggleHtml = cardHtml('new', T1);

    const subtasks = {
        openHtml: openHtml,
        emptyHtml: emptyHtml,
        emptyErrorHtml: emptyErrorHtml,
        createRequests: createRequests.slice(0, createBefore),
        createdRequests: createRequests.slice(createBefore),
        patchRequests: patchRequests.slice(),
        afterAddHtml: afterAddHtml,
        afterToggleHtml: afterToggleHtml,
    };

    // ---- перетаскивание: успех ----
    const dragStart = (id) => targetEvent({ '[data-board-card]': { dataset: { boardCard: id } } });
    const columnEvent = (status, cardId) => targetEvent({
        '[data-column]': { dataset: { column: status } },
        '[data-board-card]': cardId
            ? {
                dataset: { boardCard: cardId },
                getBoundingClientRect() { return { top: 0, height: 40 }; },
            }
            : null,
    }, { clientY: 30 });

    const started = dragStart(T1);
    documentListeners.dragstart(started);
    const dragStarted = app.state.dragging === true;

    const over = columnEvent('in_progress', null);
    documentListeners.dragover(over);
    await documentListeners.drop(columnEvent('in_progress', null));
    await waitFor(() => moveRequests.length > 0, 'POST move');
    await wait();

    const inProgressHtml = boardBody('in_progress').children.map((node) => node.innerHTML).join('\n');
    const newHtmlAfterMove = boardBody('new').children.map((node) => node.innerHTML).join('\n');

    // ---- перетаскивание: ошибка сети возвращает карточку ----
    failMoves = true;
    documentListeners.dragstart(dragStart(T2));
    documentListeners.dragover(columnEvent('done', null));
    await documentListeners.drop(columnEvent('done', null));
    await waitFor(() => moveRequests.length === 2, 'неудачный POST move');
    await wait();

    const move = {
        dragStarted: dragStarted,
        requests: moveRequests.slice(),
        inProgressHtml: inProgressHtml,
        newHtml: newHtmlAfterMove,
        signature: boardBody('in_progress').children.length
            ? boardBody('in_progress').children[0].dataset.listSig || ''
            : '',
        banner: elementById('banner').textContent,
        doneHtml: boardBody('done').children.map((node) => node.innerHTML).join('\n'),
    };
    failMoves = false;

    // ---- кнопка статуса на карточке: тот же PATCH, что и в панели задачи ----
    const boardStatusEvent = (id, nextStatus) => targetEvent({
        '[data-board-status]': { dataset: { boardStatus: nextStatus, boardStatusId: id } },
    });

    await clickHandler(boardStatusEvent(T3, 'done'));
    await waitFor(() => patchRequests.length === 2, 'PATCH статуса карточки');
    await wait();

    const status = {
        requests: patchRequests.slice(1),
        moves: moveRequests.length,
        doneHtml: boardBody('done').children.map((node) => node.innerHTML).join('\n'),
        newHtml: boardBody('new').children.map((node) => node.innerHTML).join('\n'),
    };

    // ---- удаление: каскад и detach ----
    const deleteEvent = (id, mode) => targetEvent({
        '[data-delete-task]': { dataset: { deleteTask: id, deleteMode: mode } },
    });

    await clickHandler(deleteEvent(T3, 'cascade'));
    await waitFor(() => deleteRequests.length === 1, 'DELETE cascade');
    await wait();
    const cascade = { confirm: deleteRequests[0].confirm, url: deleteRequests[0].url };

    await clickHandler(deleteEvent(T2, 'detach'));
    await waitFor(() => deleteRequests.length === 2, 'DELETE detach');
    await wait();
    const detach = { confirm: deleteRequests[1].confirm, url: deleteRequests[1].url };

    // Считаем карточки по счётчикам колонок: пустая колонка держит заглушку «Пока пусто».
    const remaining = Number(elementById('board-count-new').textContent)
        + Number(elementById('board-count-in_progress').textContent);

    const deletes = { cascade: cascade, detach: detach, remaining: remaining };

    // ---- мобильная ширина: drag выключен, вкладки показывают одну колонку ----
    desktop = false;
    const mobileDrag = dragStart(T1);
    documentListeners.dragstart(mobileDrag);
    desktop = true;

    elementById('board-tabs').listeners.click({
        target: {
            closest(selector) {
                return selector === '[data-board-tab]' ? { dataset: { boardTab: 'done' } } : null;
            },
        },
    });

    const mobile = {
        prevented: mobileDrag.prevented,
        dragging: app.state.dragging === true,
        moveRequests: moveRequests.length,
        doneActive: elementById('board-column-done').classList.contains('is-active'),
        newActive: elementById('board-column-new').classList.contains('is-active'),
    };

    // ---- «Отменено»: развернуть и свернуть ----
    const headListener = elementById('board-head-cancelled').listeners.click;
    headListener();
    const expandedText = elementById('board-toggle-cancelled').textContent;
    const expanded = elementById('board-column-cancelled').classList.contains('board-column-collapsed');
    headListener();
    const collapsedText = elementById('board-toggle-cancelled').textContent;
    const collapsed = elementById('board-column-cancelled').classList.contains('board-column-collapsed');

    const cancelled = {
        expandedText: expandedText,
        expanded: expanded,
        collapsedText: collapsedText,
        collapsed: collapsed,
    };

    // ---- автообновление: тик молчит при перетаскивании и вводе подзадачи ----
    const tick = { dragging: 0, editing: 0, normal: 0 };
    for (const flag of ['dragging', 'editing']) {
        const before = requests.length;
        app.state[flag] = true;
        refreshTick();
        await wait();
        tick[flag] = requests.length - before;
        app.state[flag] = false;
    }
    const beforeNormal = requests.length;
    refreshTick();
    await wait();
    tick.normal = requests.length - beforeNormal;

    // ---- экранирование: заголовок и тег с HTML не попадают в разметку сырыми ----
    const EVIL_ID = '66666666-6666-6666-6666-666666666666';
    const EVIL_TITLE = '<img src=x onerror=alert(1)>';
    const EVIL_TAG = '<img src=x onerror=alert(2)>';
    rootTasks.push(rootTask(EVIL_ID, EVIL_TITLE, 'new', 3, [EVIL_TAG], 0, 0));
    refreshTick();
    await waitFor(() => cardHtml('new', EVIL_ID).length > 0, 'карточка с опасным заголовком');
    const escaping = { html: cardHtml('new', EVIL_ID) };

    process.stdout.write(JSON.stringify({
        board: board,
        firstClick: firstClick,
        secondClick: secondClick,
        taskCard: taskCard,
        subtasks: subtasks,
        move: move,
        status: status,
        deletes: deletes,
        mobile: mobile,
        cancelled: cancelled,
        tick: tick,
        escaping: escaping,
    }));
})().catch((error) => {
    process.stderr.write(String((error && error.stack) || error));
    process.exit(1);
});
""";
}

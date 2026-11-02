using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Xunit;
using Xunit.Sdk;

namespace Kouki.Tests;

/// <summary>
/// Поведение дашборда: <c>app.js</c> исполняется в node с поддельным DOM, а тест смотрит
/// на получившуюся разметку и на состояние фильтра, а не на подстроки в исходнике.
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
    public void Cards_show_tag_chips_and_a_click_toggles_the_filter()
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

            JsonNode result = JsonNode.Parse(stdout)
                ?? throw new XunitException("node вернул не JSON: " + stdout);

            // Карточки построены шаблоном app.js: у задачи с тегом чип есть, у задачи без
            // тегов его быть не должно (иначе чипы «прилипают» от соседней карточки).
            JsonArray cards = result["cards"]!.AsArray();
            Assert.Equal(2, cards.Count);

            string taggedCard = cards[0]!.GetValue<string>();
            string plainCard = cards[1]!.GetValue<string>();

            Assert.Contains("data-tag=\"работа\"", taggedCard, StringComparison.Ordinal);
            Assert.Contains(">#работа<", taggedCard, StringComparison.Ordinal);
            Assert.DoesNotContain("data-tag", plainCard, StringComparison.Ordinal);

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

    [Fact]
    public void Task_card_shows_recognition_history_and_loads_audio_on_demand()
    {
        string appJs = Path.Combine(TestPaths.RepoRoot(), "Kagedama.api", "wwwroot", "app.js");
        Assert.True(File.Exists(appJs), $"нет файла дашборда: {appJs}");

        string directory = Path.Combine(Path.GetTempPath(), "kouki-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "dashboard-phrases-behavior.test.js");

        try
        {
            File.WriteAllText(script, Harness);

            (int exitCode, string stdout, string stderr) = RunNode(script, appJs);
            Assert.True(
                exitCode == 0,
                $"node завершился с кодом {exitCode}:{Environment.NewLine}{stderr}");

            JsonNode result = JsonNode.Parse(stdout)
                ?? throw new XunitException("node вернул не JSON: " + stdout);
            JsonNode card = result["taskCard"]
                ?? throw new XunitException("харнесс не вернул карточку задачи: " + stdout);

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
    /// Минимум, который нужен скрипту: элементы с <c>dataset</c>/<c>children</c>/<c>innerHTML</c>,
    /// <c>document.createElement('template')</c> с <c>content.firstElementChild</c> (так работает
    /// <c>applyList</c>) и запоминание глобальных обработчиков клика. <c>fetch</c> отвечает
    /// двумя задачами — с тегом и без; <c>setInterval</c> не запускается: тесту нужен один
    /// предсказуемый цикл, а не автообновление.
    /// </remarks>
    private const string Harness = """
'use strict';
// Дашборд в песочнице: app.js исполняется целиком, но вместо браузера — поддельный DOM.
// Скрипт печатает один JSON; утверждения делает C#-тест.
const fs = require('fs');

const appPath = process.argv[2];
const source = fs.readFileSync(appPath, 'utf8');

const AUDIO_ID = '33333333-3333-3333-3333-333333333333';

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
        taskId: '11111111-1111-1111-1111-111111111111',
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
        taskId: '11111111-1111-1111-1111-111111111111',
        hasAudio: false,
    },
];

// Сценарий теста переключает выдачу: после проигрывания аудио фразы «исчезают».
let classifications = CLASSIFICATIONS;

const TASKS = [
    {
        id: '11111111-1111-1111-1111-111111111111',
        status: 'new',
        priority: 2,
        title: 'ответить на письмо',
        source: 'api',
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        tags: ['работа'],
    },
    {
        id: '22222222-2222-2222-2222-222222222222',
        status: 'new',
        priority: 2,
        title: 'позвонить маме',
        source: 'api',
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        tags: [],
    },
];

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
        classList: {
            add() {},
            remove() {},
            toggle() {},
            contains() { return false; },
        },
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

const requests = [];
const calls = [];
const urlsCreated = [];
const urlsRevoked = [];

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
    requests.push(href);
    calls.push({ url: href, options: options || {} });
    if (href.indexOf('/api/audio/') >= 0) return blobResponse();
    if (href.indexOf('/api/classifications') >= 0) return jsonResponse(classifications);
    if (href.indexOf('/api/actions') >= 0) return jsonResponse([]);
    if (/\/api\/tasks\/[0-9a-f-]+/.test(href)) return jsonResponse(TASKS[0]);
    if (href.indexOf('/api/tasks') >= 0) return jsonResponse(TASKS);
    if (href.indexOf('/api/health') >= 0) return jsonResponse({ utc: new Date().toISOString() });
    return jsonResponse([]);
}

const sandboxConsole = { info() {}, log() {}, warn() {}, error() {} };
const sandboxSetInterval = function () { return 0; };

const app = new Function(
    'document',
    'localStorage',
    'fetch',
    'setInterval',
    'console',
    'URL',
    source + '\nreturn { state: state };'
)(document, localStorage, fakeFetch, sandboxSetInterval, sandboxConsole, sandboxURL);

const wait = () => new Promise((resolve) => setTimeout(resolve, 0));

function tasksRequests() {
    return requests.filter((url) => url.indexOf('/api/tasks') >= 0);
}

function lastTasksUrl() {
    const all = tasksRequests();
    return all.length ? all[all.length - 1] : '';
}

(async () => {
    const list = elementById('task-list');

    // Стартовый цикл дашборда: switchTab -> reload -> loadTasks.
    for (let i = 0; i < 200 && list.children.length < 2; i += 1) {
        await wait();
    }

    const cards = list.children.map((node) => node.innerHTML);
    if (cards.length !== 2) {
        throw new Error('карточек ожидалось 2, получено ' + cards.length);
    }

    const clickHandler = documentListeners['click'];
    if (typeof clickHandler !== 'function') {
        throw new Error('нет глобального обработчика клика');
    }

    const chip = { dataset: { tag: 'работа' } };
    let prevented = 0;
    const makeEvent = () => ({
        target: {
            closest(selector) { return selector === '[data-tag]' ? chip : null; },
        },
        preventDefault() { prevented += 1; },
        stopPropagation() {},
    });

    await clickHandler(makeEvent());
    await wait();
    const firstClick = { tag: app.state.tag, url: lastTasksUrl(), prevented: prevented };

    await clickHandler(makeEvent());
    await wait();
    const secondClick = { tag: app.state.tag, url: lastTasksUrl() };

    // ---- карточка задачи: история распознавания и ленивая загрузка аудио ----
    function taskEvent(taskId) {
        return {
            target: {
                closest(selector) {
                    if (selector === '[data-task]') return { dataset: { task: taskId } };
                    return null;
                },
            },
            preventDefault() {},
            stopPropagation() {},
        };
    }

    function playEvent(audioId) {
        return {
            target: {
                closest(selector) {
                    if (selector === '[data-audio-play]') return { dataset: { audioPlay: audioId } };
                    return null;
                },
            },
            preventDefault() {},
            stopPropagation() {},
        };
    }

    const detailBody = elementById('detail-body');

    await clickHandler(taskEvent(TASKS[0].id));
    for (let i = 0; i < 200 && detailBody.innerHTML.indexOf('История распознавания') < 0; i += 1) {
        await wait();
    }

    const detailHtml = detailBody.innerHTML;
    const classificationRequests = requests.filter((url) => url.indexOf('/api/classifications?taskId=') >= 0);
    const audioRequestsBeforeClick = requests.filter((url) => url.indexOf('/api/audio/') >= 0).length;
    const revokedBeforeReopen = urlsRevoked.slice();

    await clickHandler(playEvent(AUDIO_ID));
    for (let i = 0; i < 200 && urlsCreated.length === 0; i += 1) {
        await wait();
    }

    const audioCalls = calls
        .filter((call) => call.url.indexOf('/api/audio/') >= 0)
        .map((call) => ({
            url: call.url,
            authorization: (call.options && call.options.headers && call.options.headers.Authorization) || '',
        }));

    // Фразы исчезли: автообновление карточки перерисовывает блок и отзывает старый URL.
    classifications = [];
    await clickHandler(taskEvent(TASKS[0].id));
    for (let i = 0; i < 200 && detailBody.innerHTML.indexOf('Связанных фраз нет.') < 0; i += 1) {
        await wait();
    }

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

    process.stdout.write(JSON.stringify({
        cards: cards,
        firstClick: firstClick,
        secondClick: secondClick,
        taskCard: taskCard,
    }));
})().catch((error) => {
    process.stderr.write(String((error && error.stack) || error));
    process.exit(1);
});
""";
}

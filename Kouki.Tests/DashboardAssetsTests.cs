using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

using Kouki.Kagedama.Api;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Xunit;

namespace Kouki.Tests;

/// <summary>
/// Дашборд — часть поставки: статика лежит в репозитории и не собирается компилятором,
/// поэтому единственная страховка от случайной поломки — эти проверки.
/// </summary>
public sealed class DashboardAssetsTests
{
    private static readonly string WebRoot = Path.Combine(TestPaths.RepoRoot(), "Kagedama.api", "wwwroot");

    [Fact]
    public void Dashboard_files_are_present_and_not_empty()
    {
        foreach (string name in new[] { "index.html", "styles.css", "app.js", "favicon.svg" })
        {
            string path = Path.Combine(WebRoot, name);
            Assert.True(File.Exists(path), $"нет файла {path}");
            Assert.True(new FileInfo(path).Length > 300, $"файл {name} подозрительно мал");
        }
    }

    [Fact]
    public void Index_references_stylesheet_and_script()
    {
        string html = File.ReadAllText(Path.Combine(WebRoot, "index.html"));

        Assert.Contains("styles.css", html, StringComparison.Ordinal);
        Assert.Contains("app.js", html, StringComparison.Ordinal);
        Assert.Contains("favicon.svg", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"spine\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Stylesheet_defines_the_ink_palette()
    {
        string css = File.ReadAllText(Path.Combine(WebRoot, "styles.css"));

        foreach (string token in new[] { "--ink:", "--sand:", "--vermilion:", "--serif:", "--mono:" })
        {
            Assert.Contains(token, css, StringComparison.Ordinal);
        }

        Assert.Contains(".tile", css, StringComparison.Ordinal);
        // Один порог мобильной вёрстки на весь дашборд — 768 px.
        Assert.Contains("768 px", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 767px)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Index_has_board_columns_mobile_tabs_and_no_status_filter()
    {
        string html = File.ReadAllText(Path.Combine(WebRoot, "index.html"));

        Assert.Contains("id=\"board\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"board-tabs\"", html, StringComparison.Ordinal);

        foreach (string status in new[] { "new", "in_progress", "needs_review", "done", "cancelled" })
        {
            Assert.Contains($"data-column=\"{status}\"", html, StringComparison.Ordinal);
            Assert.Contains($"data-board-tab=\"{status}\"", html, StringComparison.Ordinal);
        }

        // Фильтр статусов больше не нужен: он скрыт, фильтр тегов остаётся.
        Assert.Matches("id=\"status-filter\"[^>]*hidden", html);
        Assert.Contains("Показать отменённые", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Stylesheet_draws_the_board_and_collapsed_cancelled_column()
    {
        string css = File.ReadAllText(Path.Combine(WebRoot, "styles.css"));

        foreach (string token in new[]
                 {
                     ".board", ".board-column", ".board-column-collapsed", ".board-tabs",
                     ".board-card", ".board-card-status", ".priority-1", ".priority-2", ".priority-3",
                     ".subtask", ".subtask-empty", ".btn.danger",
                 })
        {
            Assert.Contains(token, css, StringComparison.Ordinal);
        }

        // Свёрнутая колонка прячет только тело, шапка и счётчик остаются.
        Assert.Contains(".board-column-collapsed .board-column-body", css, StringComparison.Ordinal);
        // На мобильной ширине видна одна колонка, остальные скрыты.
        Assert.Contains(".board-column.is-active", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_keeps_the_board_contract_texts_and_constants()
    {
        string app = File.ReadAllText(Path.Combine(WebRoot, "app.js"));

        foreach (string token in new[]
                 {
                     "BOARD_LIMIT = 500", "AUTO_REFRESH_MS = 5000", "MOBILE_BREAKPOINT = 768",
                     "board-column-collapsed", "data-subtask-toggle", "data-board-status",
                     "data-board-status-id", "task.position", "Подзадач нет",
                     "Введите текст подзадачи.", "Не удалось создать подзадачу.",
                     "Не удалось удалить задачу.", "Не удалось переместить задачу.",
                     "Показать отменённые", "Свернуть",
                     "Удалить задачу вместе со всеми подзадачами? Подзадач:",
                     "Удалить задачу? Подзадачи станут самостоятельными. Подзадач:",
                     "mode=cascade&confirm=true", "mode=detach",
                     "state.dragging", "state.editing",
                 })
        {
            Assert.Contains(token, app, StringComparison.Ordinal);
        }

        // move возвращает пустые tags (ограничение П2): карточку из ответа не перерисовывают.
        Assert.DoesNotContain("moved.tags", app, StringComparison.Ordinal);
        // Фильтр статусов больше не управляет запросом доски.
        Assert.DoesNotContain("params.set('status'", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_element_looked_up_by_script_exists_in_markup()
    {
        string html = File.ReadAllText(Path.Combine(WebRoot, "index.html"));
        string app = File.ReadAllText(Path.Combine(WebRoot, "app.js"));

        var declared = Regex.Matches(html, "id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var looked = Regex.Matches(app, "\\$\\('([^']+)'\\)").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(app, "setCount\\('([^']+)'").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var missing = looked.Where(id => !declared.Contains(id)).ToArray();
        Assert.True(missing.Length == 0, "app.js обращается к отсутствующим id: " + string.Join(", ", missing));
    }

    // Автообновление не должно перерисовывать разметку: иначе сбрасывается ввод
    // в поле «своя метка» и прыгает прокрутка таймлайна. Проверяем, что механизм
    // точечного обновления и защита «пока печатают — не трогать» на месте.
    [Fact]
    public void Script_updates_lists_in_place_and_respects_typing()
    {
        string app = File.ReadAllText(Path.Combine(WebRoot, "app.js"));

        Assert.Contains("function applyList(", app, StringComparison.Ordinal);
        Assert.Contains("data-list-key", app, StringComparison.Ordinal);
        Assert.Contains("dataset.listSig", app, StringComparison.Ordinal);
        Assert.Contains("function isUserBusy(", app, StringComparison.Ordinal);
        Assert.Contains("replaceChildren(...ordered)", app, StringComparison.Ordinal);

        // полная перерисовка списков через innerHTML не должна вернуться
        Assert.DoesNotContain("list.innerHTML = tasks.map", app, StringComparison.Ordinal);
        Assert.DoesNotContain("body.innerHTML = actions.map", app, StringComparison.Ordinal);
    }

    // Живой деплой 02.11 показал две дыры, которые ловятся только на боевом браузере:
    // старый app.js из эвристического кэша поверх нового HTML (Cannot read properties of
    // null (reading 'dataset')) и видимый #status-filter, потому что .chips{display:flex}
    // перебивал атрибут hidden. Заголовки проверяются на настоящем Kestrel с боевыми
    // настройками из DashboardStaticFiles: грепать Program.cs бессмысленно.
    [Fact]
    public async Task Static_files_are_served_without_heuristic_cache()
    {
        using PhysicalFileProvider files = DashboardStaticFiles.Provider(WebRoot);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Test",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        await using WebApplication app = builder.Build();
        DashboardStaticFiles.Use(app, files);
        await app.StartAsync();

        try
        {
            IServer server = app.Services.GetRequiredService<IServer>();
            IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel не сообщил адрес запуска.");
            using var client = new HttpClient { BaseAddress = new Uri(addresses.Addresses.First()) };

            HttpResponseMessage page = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("no-cache", page.Headers.CacheControl?.ToString());
            Assert.False(string.IsNullOrEmpty(page.Headers.ETag?.ToString()), "у index.html должен быть ETag");

            HttpResponseMessage script = await client.GetAsync("/app.js?v=2026-11-02.1");
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Equal("no-cache", script.Headers.CacheControl?.ToString());
            Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);

            // Повторный запрос с ETag: ответ 304 обязан сохранять no-cache, иначе браузер
            // снова начнёт применять к нему эвристику.
            using var revalidate = new HttpRequestMessage(HttpMethod.Get, "/app.js?v=2026-11-02.1");
            revalidate.Headers.TryAddWithoutValidation("If-None-Match", script.Headers.ETag!.ToString());
            HttpResponseMessage notModified = await client.SendAsync(revalidate);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
            Assert.Equal("no-cache", notModified.Headers.CacheControl?.ToString());

            HttpResponseMessage styles = await client.GetAsync("/styles.css?v=2026-11-02.1");
            Assert.Equal(HttpStatusCode.OK, styles.StatusCode);
            Assert.Equal("no-cache", styles.Headers.CacheControl?.ToString());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    // Браузера в тестовом окружении нет, поэтому каскад закодирован структурно: правило
    // [hidden] обязано лежать на верхнем уровне (не внутри @media) и побеждать !important,
    // а .chips не должен включать display: flex при наличии атрибута hidden.
    [Fact]
    public void Index_has_telemetry_tab_and_panel()
    {
        string html = File.ReadAllText(Path.Combine(WebRoot, "index.html"));

        Assert.Contains("data-tab=\"telemetry\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"tab-telemetry\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"count-telemetry\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"panel-telemetry\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"telemetry-requests\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"telemetry-errors\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"telemetry-avg-duration\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"telemetry-audio-bytes\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"telemetry-endpoints-table\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"telemetry-sources-table\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_implements_telemetry_requests_and_tab_handling()
    {
        string app = File.ReadAllText(Path.Combine(WebRoot, "app.js"));

        Assert.Contains("/api/telemetry", app, StringComparison.Ordinal);
        Assert.Contains("loadTelemetry", app, StringComparison.Ordinal);
        Assert.Contains("telemetryHours", app, StringComparison.Ordinal);
        Assert.Contains("data-telemetry-hours", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Cascade_keeps_hidden_attribute_effective()
    {
        string css = File.ReadAllText(Path.Combine(WebRoot, "styles.css"));

        // Правило ровно одно: если оно есть ещё раз ниже, оно перебило бы первое.
        int rule = css.IndexOf("[hidden] {", StringComparison.Ordinal);
        Assert.True(rule > 0, "нет правила [hidden]");
        Assert.Equal(rule, css.LastIndexOf("[hidden] {", StringComparison.Ordinal));
        Assert.Equal(0, BraceDepthAt(css, rule));

        int end = css.IndexOf('}', rule);
        Assert.Contains("display: none !important", css[rule..end], StringComparison.Ordinal);

        // Ищем начало правила, а не подстроку: закомментированный или упомянутый в
        // другом селекторе .chips:not([hidden]) не спасает каскад.
        Assert.Equal(1, CountOf(css, "\n.chips:not([hidden]) {"));
        Assert.Equal(0, BraceDepthAt(css, css.IndexOf("\n.chips:not([hidden]) {", StringComparison.Ordinal)));
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        int index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static int BraceDepthAt(string css, int position)
    {
        int depth = 0;
        for (int i = 0; i < position; i++)
        {
            if (css[i] == '{')
            {
                depth++;
            }
            else if (css[i] == '}')
            {
                depth--;
            }
        }

        return depth;
    }

    // Чипы тегов в карточке и фильтр по клику проверяются поведенчески:
    // Kouki.Tests/DashboardBehaviorTests.cs исполняет app.js в node с поддельным DOM.
    // Грепать подстроки здесь бессмысленно — удаление ${tagChips(task.tags)} из шаблона
    // оставляет все подстроки на месте, а чипы с экрана пропадают.
}

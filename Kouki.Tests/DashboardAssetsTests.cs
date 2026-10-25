using System.Text.RegularExpressions;
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
        Assert.Contains("@media (max-width: 760px)", css, StringComparison.Ordinal);
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
}

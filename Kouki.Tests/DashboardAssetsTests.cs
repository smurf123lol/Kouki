using System.Text.RegularExpressions;
using Xunit;

namespace Kouki.Tests;

/// <summary>
/// Дашборд — часть поставки: статика лежит в репозитории и не собирается компилятором,
/// поэтому единственная страховка от случайной поломки — эти проверки.
/// </summary>
public sealed class DashboardAssetsTests
{
    private static readonly string WebRoot = Path.Combine(RepoRoot(), "Kagedama.api", "wwwroot");

    private static string RepoRoot()
    {
        var assembly = typeof(DashboardAssetsTests).Assembly;
        string? root = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepoRoot")
            ?.Value;

        Assert.False(string.IsNullOrWhiteSpace(root), "в сборку тестов не попал RepoRoot (AssemblyMetadata)");
        return root!;
    }

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
}

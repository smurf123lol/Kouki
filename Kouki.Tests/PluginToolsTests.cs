using System.Text.RegularExpressions;
using Xunit;

namespace Kouki.Tests;

/// <summary>
/// Плагин <c>dsh-kouki</c> — JS, который компилятор не проверяет. Его инструменты описаны
/// руками, и расхождение с MCP-сервером (5 из 7, как было до C-27) видно только здесь.
/// </summary>
public sealed class PluginToolsTests
{
    private static readonly string IndexJs = Path.Combine(RepoRoot(), "dsh-kouki", "index.js");

    private static string RepoRoot()
    {
        var assembly = typeof(PluginToolsTests).Assembly;
        string? root = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepoRoot")
            ?.Value;

        Assert.False(string.IsNullOrWhiteSpace(root), "в сборку тестов не попал RepoRoot (AssemblyMetadata)");
        return root!;
    }

    [Fact]
    public void Plugin_exposes_every_mcp_tool()
    {
        Assert.True(File.Exists(IndexJs), $"нет файла {IndexJs}");
        string source = File.ReadAllText(IndexJs);

        string[] expected =
        [
            "kouki_add_task",
            "kouki_list_tasks",
            "kouki_complete_task",
            "kouki_unresolved_classifications",
            "kouki_label_classification",
            "kouki_task_history",
            "kouki_context",
        ];

        foreach (string name in expected)
        {
            Assert.Contains($"name: \"{name}\"", source, StringComparison.Ordinal);
        }

        MatchCollection declared = Regex.Matches(source, "name: \"(kouki_[a-z_]+)\"");
        Assert.Equal(expected.Length, declared.Count);
    }

    [Fact]
    public void Plugin_readme_lists_every_tool()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "dsh-kouki", "README.md"));
        foreach (string name in new[] { "kouki_task_history", "kouki_context" })
        {
            Assert.Contains(name, readme, StringComparison.Ordinal);
        }
    }
}

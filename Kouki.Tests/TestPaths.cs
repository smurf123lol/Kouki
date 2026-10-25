namespace Kouki.Tests;

/// <summary>
/// Пути, которые тестам нужны от репозитория (артефакты, не собираемые
/// компилятором: статика дашборда, конфиги deploy).
/// </summary>
/// <remarks>
/// Тесты запускаются из разных каталогов (в том числе из worktree задачи),
/// поэтому корень репозитория приходит из сборки — <c>AssemblyMetadata</c>
/// <c>RepoRoot</c>, который выставляет <c>Kouki.Tests.csproj</c>. Один общий
/// помощник: иначе каждый тест придумывает свой способ искать корень и половина
/// находит посторонний <c>Kouki.slnx</c>.
/// </remarks>
internal static class TestPaths
{
    /// <summary>Абсолютный путь к корню репозитория (<c>Kouki.slnx</c>).</summary>
    public static string RepoRoot()
    {
        var assembly = typeof(TestPaths).Assembly;
        string? root = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepoRoot")
            ?.Value;

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "в сборку тестов не попал RepoRoot (AssemblyMetadata) — смотрите Kouki.Tests.csproj");
        }

        return root;
    }

    /// <summary>Путь внутри репозитория, например <c>Path("deploy", "files", "README.md")</c>.</summary>
    public static string Path(params string[] parts)
    {
        string result = RepoRoot();
        foreach (string part in parts)
        {
            result = System.IO.Path.Combine(result, part);
        }

        return result;
    }
}

using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

/// <summary>
/// Режим тегов из конфигурации: extract/keep на источник и fail-closed проверка при старте.
/// </summary>
public sealed class TagModeTests
{
    [Fact]
    public void Unknown_default_mode_breaks_startup()
    {
        KoukiHostSettings settings = new()
        {
            DatabasePath = TempDatabasePath(),
            TagMode = "extarct",
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddKoukiCore(settings));

        Assert.Contains("Неизвестный режим тегов", error.Message, StringComparison.Ordinal);
        Assert.Contains("extarct", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_source_in_override_breaks_startup()
    {
        KoukiHostSettings settings = new()
        {
            DatabasePath = TempDatabasePath(),
            TagModeBySource = new Dictionary<string, string> { ["телепорт"] = TagModes.Extract },
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddKoukiCore(settings));

        Assert.Contains("телепорт", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_mode_in_override_breaks_startup()
    {
        KoukiHostSettings settings = new()
        {
            DatabasePath = TempDatabasePath(),
            TagModeBySource = new Dictionary<string, string> { [TaskSources.Station] = "keepo" },
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddKoukiCore(settings));

        Assert.Contains("keepo", error.Message, StringComparison.Ordinal);
        Assert.Contains(TaskSources.Station, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_is_resolved_per_source()
    {
        KoukiHostSettings settings = new()
        {
            DatabasePath = TempDatabasePath(),
            TagMode = TagModes.Extract,
            TagModeBySource = new Dictionary<string, string> { [TaskSources.Station] = TagModes.Keep },
        };

        var services = new ServiceCollection();
        services.AddKoukiCore(settings);

        using ServiceProvider provider = services.BuildServiceProvider();
        IntakeOptions options = provider.GetRequiredService<IntakeOptions>();

        // Станция диктует метку голосом — решётки из текста не вырезаем; у api режим по умолчанию.
        Assert.Equal(TagModes.Keep, options.ResolveTagMode(TaskSources.Station));
        Assert.Equal(TagModes.Extract, options.ResolveTagMode(TaskSources.Api));
        Assert.Equal(TagModes.Extract, options.ResolveTagMode(null));
    }

    private static string TempDatabasePath()
        => Path.Combine(Path.GetTempPath(), "kouki-tagmode-" + Guid.NewGuid().ToString("N") + ".sqlite");
}

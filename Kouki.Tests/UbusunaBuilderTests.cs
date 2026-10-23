using Kouki.Shimi.Context;
using Kouki.Shimi.DependencyInjection;
using Kouki.Shimi.ServiceAbstractions;
using Kouki.Ubusuna;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

/// <summary>Проверки UbusunaBuilder: один контейнер, реестр заполняется, default назначается.</summary>
public sealed class UbusunaBuilderTests
{
    [Fact]
    public void Build_registers_adapters_and_declared_context_as_default()
    {
        var services = new ServiceCollection();

        new UbusunaBuilder(services)
            .UseConsoleAdapters()
            .AddContext("custom", provider => new BaseExecutionContext(
                "custom",
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<ITerminal>(),
                new InMemoryStateStore()))
            .SetDefaultContext("custom")
            .Build();

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IContextRegistry>();

        Assert.True(registry.TryGetDefaultContext(out var defaultContext));
        Assert.Equal("custom", defaultContext.Name);
        Assert.Equal("custom", registry.GetContext("custom")!.Name);

        Assert.IsType<Kouki.Ubusuna.Adapters.ConsoleFileSystem>(provider.GetRequiredService<IFileSystem>());
        Assert.IsType<Kouki.Ubusuna.Adapters.ConsoleTerminal>(provider.GetRequiredService<ITerminal>());
    }

    [Fact]
    public void Unknown_default_context_name_produces_console_context()
    {
        var services = new ServiceCollection();

        new UbusunaBuilder(services)
            .UseConsoleAdapters()
            .SetDefaultContext("fallback")
            .Build();

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IContextRegistry>();

        Assert.True(registry.TryGetDefaultContext(out var defaultContext));
        Assert.Equal("fallback", defaultContext.Name);
        Assert.NotNull(registry.GetContext("fallback")!.FileSystem);
    }

    [Fact]
    public void Build_overrides_registry_from_add_shimi_context()
    {
        var services = new ServiceCollection();
        services.AddShimiContext();

        new UbusunaBuilder(services)
            .UseConsoleAdapters()
            .SetDefaultContext("ubusuna-default")
            .Build();

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IContextRegistry>();

        Assert.True(registry.TryGetDefaultContext(out var defaultContext));
        Assert.Equal("ubusuna-default", defaultContext.Name);
        Assert.NotNull(provider.GetRequiredService<IStateStore>());
    }

    [Fact]
    public void Second_build_throws()
    {
        var services = new ServiceCollection();
        var builder = new UbusunaBuilder(services).UseConsoleAdapters();

        builder.Build();

        Assert.Throws<InvalidOperationException>(builder.Build);
    }
}

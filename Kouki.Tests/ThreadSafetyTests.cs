using Kouki.Shimi.Context;

namespace Kouki.Tests;

/// <summary>Конкурентный доступ к реестру контекстов и хранилищу состояния.</summary>
public sealed class ThreadSafetyTests
{
    [Fact]
    public void Registry_handles_parallel_registration()
    {
        var registry = new ContextRegistry();

        Parallel.For(0, 1_000, i => registry.RegisterContext($"ctx-{i}", TestHost.Context($"ctx-{i}")));

        for (var i = 0; i < 1_000; i++)
        {
            Assert.Equal($"ctx-{i}", registry.GetContext($"ctx-{i}")!.Name);
        }
    }

    [Fact]
    public void Registry_default_context_is_visible_from_other_threads()
    {
        var registry = new ContextRegistry();
        registry.RegisterContext("main", TestHost.Context("main"));

        Assert.False(registry.TryGetDefaultContext(out _));

        Parallel.For(0, 100, _ => registry.SetDefaultContext("main"));

        Assert.True(registry.TryGetDefaultContext(out var context));
        Assert.Equal("main", context.Name);

        // Неизвестное имя откатывается на контекст по умолчанию.
        Assert.Equal("main", registry.GetContext("missing")!.Name);
    }

    [Fact]
    public async Task State_store_handles_parallel_writes()
    {
        var store = new InMemoryStateStore();

        await Parallel.ForAsync(0, 1_000, async (i, _) => await store.SetAsync($"key-{i}", i));

        for (var i = 0; i < 1_000; i++)
        {
            Assert.Equal(i, await store.GetAsync<int>($"key-{i}"));
        }

        Assert.Equal(0, await store.GetAsync<int>("absent"));
    }
}

using Kouki.Shimi.Context;
using Kouki.Shimi.ServiceAbstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

public sealed class ExecutionContextTests
{
    [Fact]
    public void Declared_services_are_visible_by_type()
    {
        var context = TestHost.Context("test");

        Assert.NotNull(context.GetService<IFileSystem>());
        Assert.NotNull(context.GetService<ITerminal>());
        Assert.NotNull(context.GetService<IStateStore>());
        Assert.Same(context.StateStore, context.GetService<IStateStore>());
    }

    [Fact]
    public void Added_service_is_returned_and_removable()
    {
        var clock = new TestClock();
        var context = (BaseExecutionContext)TestHost.Context("test");

        context.AddService<IClock>(clock);

        Assert.Same(clock, context.GetService<IClock>());
        Assert.True(context.RemoveService<IClock>());
        Assert.Null(context.GetService<IClock>());
    }

    [Fact]
    public void Iron_services_cannot_be_removed()
    {
        var context = (BaseExecutionContext)TestHost.Context("test");

        Assert.False(context.RemoveService<IFileSystem>());
        Assert.False(context.RemoveService<ITerminal>());
        Assert.False(context.RemoveService<IStateStore>());
        Assert.NotNull(context.GetService<IFileSystem>());
    }

    [Fact]
    public void Missing_service_returns_null_and_try_returns_false()
    {
        var context = TestHost.Context("test");

        Assert.Null(context.GetService<IClock>());
        Assert.False(context.TryGetService<IClock>(out IClock? clock));
        Assert.Null(clock);
    }

    [Fact]
    public void Required_service_throws_with_context_name_and_usage()
    {
        var context = TestHost.Context("console");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => context.GetRequiredService<IClock>("метка времени в логе"));

        Assert.Contains("console", error.Message, StringComparison.Ordinal);
        Assert.Contains("метка времени в логе", error.Message, StringComparison.Ordinal);
        Assert.Contains("IClock", error.Message, StringComparison.Ordinal);
        Assert.Contains("AddService", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_service_returns_registered_instance()
    {
        var clock = new TestClock();
        var context = (BaseExecutionContext)TestHost.Context("test");
        context.AddService<IClock>(clock);

        Assert.Same(clock, context.GetRequiredService<IClock>());
    }

    [Fact]
    public void Service_provider_is_used_as_fallback()
    {
        var clock = new TestClock();
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IClock>(clock)
            .BuildServiceProvider();

        var context = new BaseExecutionContext(
            "server",
            new StubFileSystem(),
            new StubTerminal(),
            new InMemoryStateStore(),
            provider);

        Assert.Same(clock, context.GetService<IClock>());
        Assert.Same(clock, context.GetRequiredService<IClock>());
        Assert.True(context.HasService<IClock>());
    }

    [Fact]
    public void Service_types_list_iron_services_and_added_ones()
    {
        var context = (BaseExecutionContext)TestHost.Context("test");
        context.AddService<IClock>(new TestClock());

        IReadOnlyCollection<Type> types = context.ServiceTypes;

        Assert.Contains(typeof(IFileSystem), types);
        Assert.Contains(typeof(ITerminal), types);
        Assert.Contains(typeof(IStateStore), types);
        Assert.Contains(typeof(IClock), types);
    }

    [Fact]
    public void Describe_services_returns_sorted_names()
    {
        var context = (BaseExecutionContext)TestHost.Context("test");
        context.AddService<IClock>(new TestClock());

        IReadOnlyList<string> names = context.DescribeServices();

        Assert.Contains("IClock", names);
        Assert.Contains("IFileSystem", names);
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), names);
    }

    private interface IClock
    {
        DateTimeOffset Now { get; }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
    }
}

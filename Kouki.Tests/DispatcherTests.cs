using Kouki.Mugura.DependencyInjection;
using Kouki.Mugura.Dispatcher;
using Microsoft.Extensions.DependencyInjection;
using Kouki.Shimi.Context;

namespace Kouki.Tests;

public class DispatcherTests
{
    [Fact]
    public async Task Passes_context_and_command_to_handler()
    {
        using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();
        var context = TestHost.Context("dispatch");

        EchoHandler.Reset();

        var result = await dispatcher.SendAsync(new EchoCommand("hi"), context);

        Assert.Same(context, EchoHandler.LastContext);
        Assert.Equal("hi", result["echo"]!.GetValue<string>());
        Assert.Equal("dispatch", result["context"]!.GetValue<string>());
    }

    [Fact]
    public async Task Creates_new_handler_instance_per_call()
    {
        using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();
        var context = TestHost.Context();

        EchoHandler.Reset();

        await dispatcher.SendAsync(new EchoCommand("a"), context);
        await dispatcher.SendAsync(new EchoCommand("b"), context);

        Assert.Equal(2, EchoHandler.InstanceCount);
        Assert.Equal(2, EchoHandler.Calls);
    }

    [Fact]
    public async Task Handler_receives_exactly_the_context_passed_in()
    {
        using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        var first = TestHost.Context("first");
        var second = TestHost.Context("second");

        EchoHandler.Reset();

        await dispatcher.SendAsync(new EchoCommand("1"), first);
        Assert.Same(first, EchoHandler.LastContext);

        await dispatcher.SendAsync(new EchoCommand("2"), second);
        Assert.Same(second, EchoHandler.LastContext);
    }

    [Fact]
    public async Task Null_context_is_rejected()
    {
        using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => dispatcher.SendAsync(new EchoCommand("x"), null!));
    }

    [Fact]
    public async Task Null_command_is_rejected()
    {
        using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<ICommandDispatcher>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => dispatcher.SendAsync<EchoCommand>(null!, TestHost.Context()));
    }
}

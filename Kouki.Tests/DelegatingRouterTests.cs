using System.Text.Json.Nodes;
using Kouki.Mugura.Dispatcher;
using Microsoft.Extensions.DependencyInjection;
using Kouki.Shimi.Context;

namespace Kouki.Tests;

public class DelegatingRouterTests
{
    private sealed class CountingRouter : DelegatingRouter
    {
        public int Calls { get; private set; }
        public IExecutionContext? LastContext { get; private set; }

        public CountingRouter(IRouter innerRouter) : base(innerRouter)
        {
        }

        public override async Task<JsonNode> RouteAsync(
            string commandName,
            JsonNode? argument,
            IExecutionContext? context = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastContext = context;
            return await base.RouteAsync(commandName, argument, context, cancellationToken);
        }
    }

    [Fact]
    public void Ctor_rejects_null_inner_router()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new CountingRouter(null!);
        });
    }

    [Fact]
    public async Task Decorator_forwards_to_inner_router()
    {
        using var provider = TestHost.Build();
        var inner = provider.GetRequiredService<IRouter>();
        var decorator = new CountingRouter(inner);
        var context = TestHost.Context("decorated");

        var result = await decorator.RouteAsync("ping", null, context);

        Assert.Equal("pong", result.GetValue<string>());
        Assert.Equal(1, decorator.Calls);
        Assert.Same(context, decorator.LastContext);
    }

    [Fact]
    public async Task Decorator_propagates_error_payload()
    {
        using var provider = TestHost.Build();
        var inner = provider.GetRequiredService<IRouter>();
        var decorator = new CountingRouter(inner);

        var result = await decorator.RouteAsync("missing", null, TestHost.Context());

        Assert.NotNull(result["error"]);
        Assert.Equal(1, decorator.Calls);
    }
}

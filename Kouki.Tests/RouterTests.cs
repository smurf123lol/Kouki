using System.Text.Json.Nodes;
using Kouki.Mugura.DependencyInjection;
using Kouki.Mugura.Dispatcher;
using Microsoft.Extensions.DependencyInjection;
using Kouki.Shimi.Context;
using Kouki.Shimi.DependencyInjection;

namespace Kouki.Tests;

public class RouterTests
{
    [Fact]
    public async Task Routes_ping_and_returns_pong()
    {
        using var provider = TestHost.Build();
        var router = provider.GetRequiredService<IRouter>();

        var result = await router.RouteAsync("ping", null, TestHost.Context());

        Assert.Equal("pong", result.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_command_returns_error_payload()
    {
        using var provider = TestHost.Build();
        var router = provider.GetRequiredService<IRouter>();

        var result = await router.RouteAsync("nope", null, TestHost.Context());

        var error = Assert.IsType<JsonObject>(result)["error"]!.GetValue<string>();
        Assert.Contains("Unknown command", error);
    }

    [Fact]
    public async Task Routes_echo_with_arguments()
    {
        using var provider = TestHost.Build();
        var router = provider.GetRequiredService<IRouter>();
        var context = TestHost.Context("route");

        EchoHandler.Reset();

        var result = await router.RouteAsync("echo", JsonNode.Parse("""{"message":"hi"}"""), context);

        Assert.Equal("hi", result["echo"]!.GetValue<string>());
        Assert.Equal("route", result["context"]!.GetValue<string>());
        Assert.Same(context, EchoHandler.LastContext);
    }

    [Fact]
    public async Task Invalid_argument_returns_error_payload()
    {
        using var provider = TestHost.Build();
        var router = provider.GetRequiredService<IRouter>();

        var result = await router.RouteAsync("echo", JsonValue.Create(42), TestHost.Context());

        Assert.NotNull(result["error"]);
    }

    [Fact]
    public async Task Failed_validation_returns_error_payload_and_skips_handler()
    {
        using var provider = TestHost.Build();
        var router = provider.GetRequiredService<IRouter>();

        EchoHandler.Reset();

        var result = await router.RouteAsync(
            "create-user",
            JsonNode.Parse("""{"name":"bob","age":200}"""),
            TestHost.Context());

        var error = result["error"]!.GetValue<string>();
        Assert.Contains("Age", error);
    }

    [Fact]
    public async Task Missing_context_is_reported_when_no_default_is_registered()
    {
        using var provider = TestHost.Build();
        var router = provider.GetRequiredService<IRouter>();

        var result = await router.RouteAsync("ping", null);

        var error = result["error"]!.GetValue<string>();
        Assert.Contains("execution context", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Default_context_from_registry_is_used()
    {
        var services = new ServiceCollection();
        services.AddMugura(TestHost.Assemblies);
        services.AddShimiContext();

        using var provider = services.BuildServiceProvider();

        var defaultContext = TestHost.Context("default-ctx");
        provider.GetRequiredService<IContextRegistry>().SetDefaultContext(defaultContext);

        var router = provider.GetRequiredService<IRouter>();
        var result = await router.RouteAsync("echo", JsonNode.Parse("""{"message":"via-default"}"""));
        var resultAsObject = Assert.IsType<JsonObject>(result);

        Assert.Equal("via-default", resultAsObject["echo"]!.GetValue<string>());
        Assert.Same(defaultContext, EchoHandler.LastContext);
    }

    [Fact]
    public async Task Handler_exception_is_converted_to_error_payload()
    {
        using var provider = TestHost.Build();
        var router = provider.GetRequiredService<IRouter>();

        var result = await router.RouteAsync("boom", null, TestHost.Context());

        var error = result["error"]!.GetValue<string>();
        Assert.Contains("boom", error);
        Assert.Contains("boom!", error);
        Assert.Equal("InvalidOperationException", result["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dangerous_command_is_not_executed_without_confirmation()
    {
        DangerHandler.Calls = 0;

        using var provider = TestHost.Build();
        var router = provider.GetRequiredService<IRouter>();

        var result = await router.RouteAsync("danger", null, TestHost.Context());

        var payload = Assert.IsType<JsonObject>(result);
        Assert.True(payload["requiresConfirmation"]!.GetValue<bool>());
        Assert.Equal("danger", payload["command"]!.GetValue<string>());
        Assert.Contains("требует подтверждения", payload["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, DangerHandler.Calls);
    }

    [Fact]
    public async Task Dangerous_command_runs_when_confirmation_allows()
    {
        DangerHandler.Calls = 0;

        var services = new ServiceCollection();
        services.AddSingleton<ICommandConfirmation, AllowAllConfirmation>();
        services.AddMugura(TestHost.Assemblies);

        using var provider = services.BuildServiceProvider();
        var router = provider.GetRequiredService<IRouter>();

        var result = await router.RouteAsync("danger", null, TestHost.Context());

        Assert.Equal("done", result["danger"]!.GetValue<string>());
        Assert.Equal(1, DangerHandler.Calls);
    }

    [Fact]
    public async Task Failing_confirmation_is_treated_as_refusal()
    {
        DangerHandler.Calls = 0;

        var services = new ServiceCollection();
        services.AddSingleton<ICommandConfirmation, ThrowingConfirmation>();
        services.AddMugura(TestHost.Assemblies);

        using var provider = services.BuildServiceProvider();
        var router = provider.GetRequiredService<IRouter>();

        var result = await router.RouteAsync("danger", null, TestHost.Context());

        Assert.True(result["requiresConfirmation"]!.GetValue<bool>());
        Assert.Equal(0, DangerHandler.Calls);
    }

    /// <summary>Подтверждение, которое падает: согласием это считаться не должно.</summary>
    private sealed class ThrowingConfirmation : ICommandConfirmation
    {
        public Task<bool> ConfirmAsync(
            Kouki.Mugura.Registry.CommandEntry entry,
            Kouki.Mugura.Commands.ICommand command,
            IExecutionContext? context,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("спросить некого");
    }
}

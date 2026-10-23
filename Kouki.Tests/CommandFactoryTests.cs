using System.Text.Json.Nodes;
using Kouki.Mugura.DependencyInjection;
using Kouki.Mugura.Factories;
using Kouki.Mugura.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

public class CommandFactoryTests
{
    [Fact]
    public void Creates_command_from_json_object()
    {
        using var provider = TestHost.Build();

        var factory = provider.GetRequiredService<ICommandFactory>();
        var entry = provider.GetRequiredService<CommandRegistry>().GetEntry("echo");

        Assert.NotNull(entry);

        var created = factory.TryCreate(entry, JsonNode.Parse("""{"message":"hello"}"""), out var command, out var error);

        Assert.True(created, error);
        var echo = Assert.IsType<EchoCommand>(command);
        Assert.Equal("hello", echo.Message);
    }

    [Fact]
    public void Null_argument_produces_command_with_defaults()
    {
        using var provider = TestHost.Build();

        var factory = provider.GetRequiredService<ICommandFactory>();
        var entry = provider.GetRequiredService<CommandRegistry>().GetEntry("echo");

        Assert.NotNull(entry);

        var created = factory.TryCreate(entry, null, out var command, out var error);

        Assert.True(created, error);
        Assert.Null(Assert.IsType<EchoCommand>(command).Message);
    }

    [Fact]
    public void Invalid_json_shape_reports_error_instead_of_throwing()
    {
        using var provider = TestHost.Build();

        var factory = provider.GetRequiredService<ICommandFactory>();
        var entry = provider.GetRequiredService<CommandRegistry>().GetEntry("echo");

        Assert.NotNull(entry);

        var created = factory.TryCreate(entry, JsonValue.Create(42), out var command, out var error);

        Assert.False(created);
        Assert.Null(command);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Create_returns_null_for_invalid_argument()
    {
        using var provider = TestHost.Build();

        var factory = provider.GetRequiredService<ICommandFactory>();
        var entry = provider.GetRequiredService<CommandRegistry>().GetEntry("echo");

        Assert.NotNull(entry);

        // Старый API (без out-параметра) не должен бросать исключение.
        Assert.Null(factory.Create(entry, JsonValue.Create(42)));
    }

    [Fact]
    public void Create_returns_instance_for_valid_argument()
    {
        using var provider = TestHost.Build();

        var factory = provider.GetRequiredService<ICommandFactory>();
        var entry = provider.GetRequiredService<CommandRegistry>().GetEntry("create-user");

        Assert.NotNull(entry);

        var command = Assert.IsType<CreateUserCommand>(
            factory.Create(entry, JsonNode.Parse("""{"name":"bob","age":30}""")));

        Assert.Equal("bob", command.Name);
        Assert.Equal(30, command.Age);
    }
}

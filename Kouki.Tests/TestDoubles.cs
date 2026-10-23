using Kouki.Mugura.Commands;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using Kouki.Mugura.Attributes;
using Kouki.Mugura.DependencyInjection;
using Kouki.Mugura.Handlers;
using Kouki.Ubusuna.Commands;
using Microsoft.Extensions.DependencyInjection;
using Kouki.Shimi.Context;
using Kouki.Shimi.ServiceAbstractions;

namespace Kouki.Tests;

/// <summary>Команда-эхо: проверяет, что аргументы реально доходят до хендлера.</summary>
public sealed record EchoCommand(string? Message) : ICommand;

/// <summary>Хендлер команды echo. Собирает статистику, на которую опираются тесты.</summary>
[Command("echo", Description = "Возвращает переданное сообщение", Category = CommandCategory.Navigation)]
public sealed class EchoHandler : ICommandHandler<EchoCommand>
{
    private static int _instances;
    private static int _calls;

    public static int InstanceCount => Volatile.Read(ref _instances);

    public static int Calls => Volatile.Read(ref _calls);

    public static IExecutionContext? LastContext { get; private set; }

    public EchoHandler()
    {
        Interlocked.Increment(ref _instances);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _instances, 0);
        Interlocked.Exchange(ref _calls, 0);
        LastContext = null;
    }

    public Task<JsonNode> HandleAsync(
        EchoCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        LastContext = context;

        var result = new JsonObject
        {
            ["echo"] = command.Message,
            ["context"] = context?.Name
        };

        return Task.FromResult<JsonNode>(result);
    }
}

/// <summary>Команда с ограничениями для проверки валидации через DataAnnotations.</summary>
public sealed record CreateUserCommand(
    [property: Required] string? Name,
    [property: Range(1, 120)] int Age) : ICommand;

[Command("create-user", Description = "Создаёт пользователя", Category = CommandCategory.Admin)]
public sealed class CreateUserHandler : ICommandHandler<CreateUserCommand>
{
    public Task<JsonNode> HandleAsync(
        CreateUserCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var result = new JsonObject
        {
            ["created"] = command.Name,
            ["age"] = command.Age
        };

        return Task.FromResult<JsonNode>(result);
    }
}

/// <summary>Команда, хендлер которой всегда падает — проверяет границу ошибок роутера.</summary>
public sealed record BoomCommand : ICommand;[Command("boom", Description = "Всегда выбрасывает исключение", Category = CommandCategory.Dangerous)]
public sealed class BoomHandler : ICommandHandler<BoomCommand>
{
    public Task<JsonNode> HandleAsync(
        BoomCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("boom!");
}

/// <summary>Команда с именем в стиле интента — проверяет маршрутизацию из «Ясаби».</summary>
public sealed record AddTaskCommand(string? Text) : ICommand;

[Command("add_task", Description = "Добавляет задачу", Category = CommandCategory.Admin)]
public sealed class AddTaskHandler : ICommandHandler<AddTaskCommand>
{
    public Task<JsonNode> HandleAsync(
        AddTaskCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var result = new JsonObject
        {
            ["created"] = true,
            ["text"] = command.Text,
            ["context"] = context?.Name
        };

        return Task.FromResult<JsonNode>(result);
    }
}

internal sealed class StubFileSystem : IFileSystem
{
    public Task<byte[]> ReadBytesAsync(string path, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task WriteBytesAsync(string path, byte[] data, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<string> GetTreeAsync(string path, bool showFiles, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}

internal sealed class StubTerminal : ITerminal
{
    public Task<string> RunCommandAsync(string command, string? workingDirectory = null, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}

/// <summary>Фабрика тестового DI-контейнера.</summary>
internal static class TestHost
{
    /// <summary>Сборки с хендлерами: тестовая и Ubusuna (там живёт ping).</summary>
    public static readonly Assembly[] Assemblies =
    {
        typeof(EchoHandler).Assembly,
        typeof(PingHandler).Assembly
    };

    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddMugura(Assemblies);
        return services.BuildServiceProvider();
    }

    public static IExecutionContext Context(string name = "test")
        => new BaseExecutionContext(name, new StubFileSystem(), new StubTerminal(), new InMemoryStateStore());
}

using Kouki.Mugura.Dispatcher;
using Kouki.Mugura.Factories;
using Kouki.Mugura.Registry;
using Kouki.Mugura.Validators;
using Microsoft.Extensions.DependencyInjection;
using Shimi.Context;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

public delegate bool FactoryDelegate(CommandEntry entry, JsonNode? argument, out ICommand? command, out string? error);
public delegate bool ValidatorDelegate(object command, out string? error);
public delegate Task<JsonNode> DispatcherDelegate(object command, CancellationToken cancellationToken);
/// <summary>
/// Основной роутер, выполняющий полный цикл обработки команды:
/// получение записи, создание DTO, валидация, диспетчеризация.
/// </summary>
/// <remarks>
/// Для повышения производительности кеширует делегаты вызова валидатора и диспетчера по типу команды.
/// Валидатор резолвится через <see cref="IServiceProvider"/> как <see cref="ICommandValidator{TCommand}"/>,
/// что позволяет переопределять его для конкретной команды.
/// Диспетчер (<see cref="ICommandDispatcher"/>) вызывается с передачей контекста выполнения (<see cref="IExecutionContext"/>),
/// если он доступен (например, из запроса).
/// При ошибке валидации возвращает структурированный ответ в виде <see cref="JsonNode"/>.
/// </remarks>
public class StaticRouter : IRouter
{
    private readonly CommandRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ICommandDispatcher _dispatcher;

    private readonly ConcurrentDictionary<Type, FactoryDelegate> _factoryDelegates = new();
    private readonly ConcurrentDictionary<Type, ValidatorDelegate> _validatorDelegates = new();
    private readonly ConcurrentDictionary<Type, DispatcherDelegate> _dispatcherDelegates = new();

    public StaticRouter(CommandRegistry registry, IServiceProvider services, ICommandDispatcher dispatcher)
    {
        _registry = registry;
        _services = services;
        _dispatcher = dispatcher;
    }

    public async Task<JsonNode> RouteAsync(string commandName, JsonNode? argument,IExecutionContext? context = null, CancellationToken ct=default)
    {
        var entry = _registry.GetEntry(commandName);
        if (entry == null) return Error($"Unknown command: {commandName}");

        // 1. Фабрика
        var factoryDel = _factoryDelegates.GetOrAdd(entry.CommandType, CreateFactoryDelegate);
        if (!factoryDel(entry, argument, out var command, out var factoryError))
            return Error(factoryError ?? "Factory failed");

        // 2. Валидатор
        var validatorDel = _validatorDelegates.GetOrAdd(entry.CommandType, CreateValidatorDelegate);
        if (!validatorDel(command!, out var validationError))
            return Error(validationError ?? "Validation failed");

        // 3. Диспетчер
        var dispatcherDel = _dispatcherDelegates.GetOrAdd(entry.CommandType, CreateDispatcherDelegate);
        return await dispatcherDel(command!, ct);
    }

    private FactoryDelegate CreateFactoryDelegate(Type commandType)
    {
        // Предположим, что фабрика реализует generic-метод CreateCommand<TCommand>
        var factory = _services.GetRequiredService<ICommandFactory>(); // или конкретный класс
        var method = typeof(ICommandFactory).GetMethod("CreateCommand")!.MakeGenericMethod(commandType);
        // Сигнатура: bool CreateCommand<T>(CommandEntry entry, JsonNode? arg, out T? cmd, out string? err)
        return (CommandEntry entry, JsonNode? arg, out ICommand? cmd, out string? err) =>
        {
            var args = new object?[] { entry, arg, null, null };
            var ok = (bool)method.Invoke(factory, args)!;
            cmd = (ICommand?)args[2];
            err = (string?)args[3];
            return ok;
        };
    }

    private ValidatorDelegate CreateValidatorDelegate(Type commandType)
    {
        var validatorType = typeof(ICommandValidator<>).MakeGenericType(commandType);
        var validator = _services.GetRequiredService(validatorType);
        var method = validatorType.GetMethod("TryValidate")!;
        // Сигнатура: bool TryValidate(TCommand command, out string? error)
        return (object cmd, out string? err) =>
        {
            var args = new object?[] { cmd, null };
            var ok = (bool)method.Invoke(validator, args)!;
            err = (string?)args[1];
            return ok;
        };
    }

    private DispatcherDelegate CreateDispatcherDelegate(Type commandType)
    {
        var method = typeof(ICommandDispatcher).GetMethod("SendAsync")!.MakeGenericMethod(commandType);
        // Сигнатура: Task<JsonNode> SendAsync<TCommand>(TCommand command, CancellationToken ct)
        return (object cmd, CancellationToken ct) =>
            (Task<JsonNode>)method.Invoke(_dispatcher, new[] { cmd, ct })!;
    }

    private static JsonNode Error(string message) => new JsonObject { ["error"] = message };
}
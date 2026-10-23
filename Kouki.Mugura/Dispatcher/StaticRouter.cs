using Kouki.Mugura.Commands;
using Kouki.Mugura.Factories;
using Kouki.Mugura.Registry;
using Kouki.Mugura.Validators;
using Kouki.Shimi.Context;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Kouki.Mugura.Dispatcher;

/// <summary>
/// Основной роутер, выполняющий полный цикл обработки команды:
/// получение записи, создание DTO, валидация, диспетчеризация.
/// </summary>
/// <remarks>
/// <para>
/// Делегаты валидации и диспетчеризации строятся один раз на тип команды
/// (<see cref="MethodInfo.CreateDelegate(Type)"/> поверх generic-методов этого класса),
/// поэтому на каждом вызове нет ни <c>MakeGenericMethod</c>, ни упаковки аргументов в массив.
/// </para>
/// <para>
/// Контекст выполнения берётся из параметра <c>context</c>; если он не передан, роутер
/// пытается получить контекст по умолчанию из <see cref="IContextRegistry"/>
/// (если тот зарегистрирован в DI).
/// </para>
/// <para>
/// Валидатор резолвится из отдельного DI-скоупа, который закрывается до вызова хендлера:
/// scoped-зависимости валидатора не «залипают» в корневом провайдере.
/// </para>
/// <para>
/// Любое исключение ниже по стеку (фабрика, валидатор, хендлер, адаптеры) превращается
/// в <c>{"error": "...", "type": "..."}</c> — граница <see cref="RouteAsync"/> не выбрасывает
/// исключения наружу, кроме <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
public class StaticRouter : IRouter
{
    private delegate bool ValidateDelegate(object validator, object command, out string? error);

    private delegate Task<JsonNode> InvokeDelegate(
        ICommandDispatcher dispatcher, object command, IExecutionContext context, CancellationToken cancellationToken);

    private readonly CommandRegistry _registry;
    private readonly ICommandFactory _factory;
    private readonly IServiceProvider _services;
    private readonly ICommandDispatcher _dispatcher;

    private readonly ConcurrentDictionary<Type, ValidateDelegate> _validatorDelegates = new();
    private readonly ConcurrentDictionary<Type, InvokeDelegate> _dispatcherDelegates = new();

    public StaticRouter(
        CommandRegistry registry,
        ICommandFactory factory,
        IServiceProvider services,
        ICommandDispatcher dispatcher)
    {
        _registry = registry;
        _factory = factory;
        _services = services;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public async Task<JsonNode> RouteAsync(
        string commandName,
        JsonNode? argument,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RouteCoreAsync(commandName, argument, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Отмена — штатный сценарий вызывающего кода, её не превращаем в error-payload.
            throw;
        }
        catch (Exception exception)
        {
            return Error($"Command '{commandName}' failed: {exception.Message}", exception.GetType().Name);
        }
    }

    private async Task<JsonNode> RouteCoreAsync(
        string commandName,
        JsonNode? argument,
        IExecutionContext? context,
        CancellationToken cancellationToken)
    {
        var entry = _registry.GetEntry(commandName);
        if (entry is null)
            return Error($"Unknown command: {commandName}");

        if (!_factory.TryCreate(entry, argument, out var command, out var factoryError) || command is null)
            return Error(factoryError ?? "Factory failed");

        var executionContext = context ?? GetDefaultContext();
        if (executionContext is null)
        {
            return Error(
                "No execution context: pass IExecutionContext to RouteAsync or register " +
                "IContextRegistry with a default context via AddShimiContext / UbusunaBuilder.");
        }

        var validatorType = typeof(ICommandValidator<>).MakeGenericType(entry.CommandType);
        using (var validationScope = _services.CreateScope())
        {
            var validator = validationScope.ServiceProvider.GetService(validatorType);
            if (validator is not null)
            {
                var validate = _validatorDelegates.GetOrAdd(entry.CommandType, CreateValidatorDelegate);
                if (!validate(validator, command, out var validationError))
                    return Error(validationError ?? "Validation failed");
            }
        }

        var dispatch = _dispatcherDelegates.GetOrAdd(entry.CommandType, CreateDispatcherDelegate);
        return await dispatch(_dispatcher, command, executionContext, cancellationToken);
    }

    private IExecutionContext? GetDefaultContext()
        => _services.GetService<IContextRegistry>()?.GetDefaultContext();

    private static ValidateDelegate CreateValidatorDelegate(Type commandType)
    {
        var method = typeof(StaticRouter)
            .GetMethod(nameof(ValidateCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(commandType);

        return (ValidateDelegate)method.CreateDelegate(typeof(ValidateDelegate));
    }

    private static InvokeDelegate CreateDispatcherDelegate(Type commandType)
    {
        var method = typeof(StaticRouter)
            .GetMethod(nameof(DispatchCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(commandType);

        return (InvokeDelegate)method.CreateDelegate(typeof(InvokeDelegate));
    }

    private static bool ValidateCore<TCommand>(object validator, object command, out string? error)
        where TCommand : class, ICommand
        => ((ICommandValidator<TCommand>)validator).TryValidate((TCommand)command, out error);

    private static Task<JsonNode> DispatchCore<TCommand>(
        ICommandDispatcher dispatcher,
        object command,
        IExecutionContext context,
        CancellationToken cancellationToken)
        where TCommand : ICommand
        => dispatcher.SendAsync((TCommand)command, context, cancellationToken);

    private static JsonNode Error(string message)
        => new JsonObject { ["error"] = message };

    private static JsonNode Error(string message, string type)
        => new JsonObject { ["error"] = message, ["type"] = type };
}

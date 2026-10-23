using Kouki.Mugura.Commands;
using Kouki.Mugura.Handlers;
using Kouki.Mugura.Scope;
using Microsoft.Extensions.DependencyInjection;
using Kouki.Shimi.Context;
using System.Text.Json.Nodes;

namespace Kouki.Mugura.Dispatcher;

/// <summary>
/// Выполняет команду в новом DI-скоупе: резолвит <see cref="ICommandHandler{TCommand}"/>
/// из скоупа и передаёт ему контекст выполнения.
/// </summary>
public class CommandDispatcher : ICommandDispatcher
{
    private readonly IScopeManager _scopeManager;

    public CommandDispatcher(IScopeManager scopeManager)
    {
        _scopeManager = scopeManager ?? throw new ArgumentNullException(nameof(scopeManager));
    }

    public async Task<JsonNode> SendAsync<TCommand>(TCommand command, IExecutionContext context, CancellationToken cancellationToken) where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return await _scopeManager.RunInScopeAsync(async sp =>
        {
            var handler = sp.GetRequiredService<ICommandHandler<TCommand>>();
            return await handler.HandleAsync(command, context, cancellationToken);
        });
    }
}
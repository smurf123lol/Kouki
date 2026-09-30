using Kouki.Mugura.Commands;
using Kouki.Mugura.Handlers;
using Kouki.Mugura.Scope;
using Microsoft.Extensions.DependencyInjection;
using Shimi.Context;
using System.Text.Json.Nodes;

namespace Kouki.Mugura.Dispatcher;
/// <summary>
/// todo
/// </summary>
public class CommandDispatcher : ICommandDispatcher
{
    private readonly IScopeManager _scopeManager;

    public CommandDispatcher(IScopeManager scopeManager)
    {
        _scopeManager = scopeManager;
    }

    public async Task<JsonNode> SendAsync<TCommand>(TCommand command, IExecutionContext context, CancellationToken cancellationToken) where TCommand : ICommand
    {
        return await _scopeManager.RunInScopeAsync(async sp =>
        {
            var handler = sp.GetRequiredService<ICommandHandler<TCommand>>();
            return await handler.HandleAsync(command, context, cancellationToken);
        });
    }
}
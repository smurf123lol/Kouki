using System.Text.Json.Nodes;
using Kouki.Mugura.Commands;
using Shimi.Context;

namespace Kouki.Mugura.Dispatcher;
/// <remarks>
/// <strong>TODO:</strong> Добавить параметр configureScope для передачи контекста в скоуп.
/// Это позволит роутерам настраивать Scoped-сервисы (например, IContext) до резолва хендлера.
/// </remarks>
public interface ICommandDispatcher
{
    Task<JsonNode> SendAsync<TCommand>(TCommand command, IExecutionContext context, CancellationToken cancellationToken = default) where TCommand : ICommand;
}
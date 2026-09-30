using Shimi.Context;
using System.Text.Json.Nodes;

namespace Kouki.Mugura.Dispatcher;

public interface IRouter
{
    Task<JsonNode> RouteAsync(string commandName, JsonNode? argument, IExecutionContext? context = null,CancellationToken cancellationToken = default);
}
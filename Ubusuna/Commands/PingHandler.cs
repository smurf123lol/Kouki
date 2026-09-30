// Kouki.Ubusuna/Commands/PingHandler.cs
using Kouki.Mugura.Attributes;
using Kouki.Mugura.Handlers;
using Shimi.Context;
using System.Text.Json.Nodes;

namespace Kouki.Ubusuna.Commands;

[Command("ping", Description = "Проверка связи", Category = CommandCategory.Navigation)]
public class PingHandler : ICommandHandler<PingCommand>
{
    public Task<JsonNode> HandleAsync(PingCommand command, IExecutionContext? context = null, CancellationToken ct = default)
    {
        return Task.FromResult<JsonNode>(JsonValue.Create("pong"));
    }
}
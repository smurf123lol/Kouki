using Kouki.Mugura.Commands;
using Kouki.Mugura.Handlers;
using Shimi.Context;
using System.Text.Json.Nodes;
/// <summary>
///  Тестовый хендлер
/// </summary>
public class TestHandler : ICommandHandler<TestCommand>
{
    public Task<JsonNode> HandleAsync(TestCommand command, IExecutionContext? context = null, CancellationToken cancellationToken=default)
    {
        // Логика: возвращаем полученное значение или сообщение
        var result = new JsonObject
        {
            ["message"] = $"Received: {command.Value ?? "null"}"
        };
        return Task.FromResult<JsonNode>(result);
    }
}
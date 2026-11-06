using Kouki.Mugura.Attributes;
using Kouki.Mugura.Commands;
using Kouki.Mugura.Handlers;
using Kouki.Shimi.Context;
using System.Text.Json.Nodes;

namespace Kouki.Tests.Fixtures;

// Команда со ссылкой на файл фраз: проверяет, что путь из [Command] доезжает
// до записи реестра без изменений. Файл для теста не нужен — важно только поле.
public sealed record PhrasesFileCommand(string? Value = null) : ICommand;

[Command(
    "fixture-phrases",
    Description = "Команда с файлом фраз",
    PhrasesFile = "Phrases/fixture-phrases.txt")]
public sealed class PhrasesFileHandler : ICommandHandler<PhrasesFileCommand>
{
    public Task<JsonNode> HandleAsync(
        PhrasesFileCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<JsonNode>(new JsonObject { ["value"] = command.Value });
}

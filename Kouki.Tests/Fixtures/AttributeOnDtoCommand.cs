using Kouki.Mugura.Attributes;
using Kouki.Mugura.Commands;
using Kouki.Mugura.Handlers;
using Kouki.Shimi.Context;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;

namespace Kouki.Tests.Fixtures;

// Ловушка, которую закрывает тест: [Command] стоит на DTO, а не на классе-хендлере.
// Такую команду CommandRegistry не увидит, поэтому она не должна попадать в Names.
[Command("dto-attr-test", Description = "Атрибут на DTO не регистрирует команду")]
public record AttributeOnDtoCommand(
    [Required(ErrorMessage = "Value is required")]
    [StringLength(10, ErrorMessage = "Value too long")]
    string? Value) : ICommand;

public sealed class AttributeOnDtoHandler : ICommandHandler<AttributeOnDtoCommand>
{
    public Task<JsonNode> HandleAsync(
        AttributeOnDtoCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<JsonNode>(new JsonObject { ["value"] = command.Value });
}

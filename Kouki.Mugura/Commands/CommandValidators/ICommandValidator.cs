using Kouki.Mugura.Registry;
using System.Text.Json.Nodes;

namespace Kouki.Mugura.Validators;

public interface ICommandValidator<TCommand> where TCommand : ICommand
{
    public bool TryValidate(TCommand command, out string? error);
}